using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

public sealed class HumanInputResponseLifecycleAdversarialSnapshotTests
{
    [Fact]
    public async Task Every_satisfied_automatic_policy_rejects_a_store_snapshot_that_omits_its_required_selection()
    {
        foreach (var policy in new[]
        {
            HumanInputResponsePolicyKind.FirstValid,
            HumanInputResponsePolicyKind.Quorum,
            HumanInputResponsePolicyKind.NamedRoles,
            HumanInputResponsePolicyKind.Merge,
        })
        {
            var completed = await CompleteAutomaticPolicyAsync(policy);
            var valid = completed.Harness.Store.CurrentSnapshot!;
            var terminal = valid.Operations[^1];
            var forged = terminal with
            {
                ResultHead = terminal.PreviousHead,
                Selection = null,
            };
            var lifecycle = new HumanInputRequestLifecycleStoreSnapshot(
                terminal.PreviousHead!,
                valid.Request.RequestVersions,
                valid.Request.Operations,
                null);
            var hostile = new HumanInputResponseLifecycleStoreSnapshot(
                lifecycle,
                valid.ResponseRequest,
                valid.Responses,
                valid.Operations.Take(valid.Operations.Count - 1).Append(forged).ToArray(),
                null);
            UseExactHostileRead(completed.Harness, hostile, forged);
            completed.Harness.Authenticator.Requests.Clear();

            var result = await completed.Harness.Service.MutateAsync(completed.Command);

            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, result.Status);
            Assert.Empty(completed.Harness.Authenticator.Requests);
        }
    }

    [Fact]
    public async Task Unsatisfied_automatic_policies_remain_pending_and_never_return_a_premature_selection()
    {
        var policies = new[]
        {
            (HumanInputResponsePolicyKind.Quorum, (int?)2, (string[]?)null),
            (HumanInputResponsePolicyKind.NamedRoles, (int?)null, new[] { "role-one", "role-two" }),
            (HumanInputResponsePolicyKind.Merge, (int?)2, new[] { "role-one", "role-two" }),
        };
        foreach (var (kind, count, roles) in policies)
        {
            var request = HumanInputResponseLifecycleTestData.Request(kind, count, roles?.ToImmutableArray());
            var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
            var command = HumanInputResponseLifecycleTestData.Submit(
                request,
                harness.Store.CurrentSnapshot!.Request.Head,
                $"pending-{kind.ToString().ToLowerInvariant()}",
                $"pending-{kind.ToString().ToLowerInvariant()}-response");
            var result = await harness.Service.MutateAsync(command);

            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, result.Status);
            Assert.Equal(HumanInputRequestLifecycleStatus.Pending, result.Projection!.LifecycleStatus);
            Assert.Null(result.Operation!.Selection);
            Assert.True(HumanInputResponseAutomaticPolicyDecision.TryEvaluate(
                request,
                "possible-selection",
                harness.Time.UtcNow,
                harness.Store.CurrentSnapshot!.Responses,
                out var premature));
            Assert.Null(premature);
        }
    }

    [Fact]
    public async Task Automatic_policy_seam_rejects_duplicate_response_and_actor_identities_even_while_pending_or_manual()
    {
        foreach (var policy in new[] { HumanInputResponsePolicyKind.ManualSelection, HumanInputResponsePolicyKind.Quorum })
        {
            var request = HumanInputResponseLifecycleTestData.Request(
                policy,
                policy == HumanInputResponsePolicyKind.Quorum ? 2 : null,
                policy == HumanInputResponsePolicyKind.ManualSelection ? ["selector-role"] : null);
            var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
            Assert.Equal(
                HumanInputResponseLifecycleMutationStatus.Committed,
                (await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                    request,
                    harness.Store.CurrentSnapshot!.Request.Head,
                    $"duplicate-seam-{policy.ToString().ToLowerInvariant()}",
                    "duplicate-seam-response"))).Status);
            var first = Assert.Single(harness.Store.CurrentSnapshot!.Responses);
            var duplicateId = HumanInputResponseArtifactHash.Apply(first with
            {
                ActorId = HumanInputResponseLifecycleTestData.Actor("user-two"),
                RespondentRoleId = "role-two",
                ResponseHash = string.Empty,
            });
            var duplicateActor = HumanInputResponseArtifactHash.Apply(first with
            {
                ResponseId = "different-response-id",
                ResponseHash = string.Empty,
            });

            Assert.False(HumanInputResponseAutomaticPolicyDecision.TryEvaluate(
                request,
                "duplicate-id-selection",
                harness.Time.UtcNow,
                [first, duplicateId],
                out _));
            Assert.False(HumanInputResponseAutomaticPolicyDecision.TryEvaluate(
                request,
                "duplicate-actor-selection",
                harness.Time.UtcNow,
                [first, duplicateActor],
                out _));
        }
    }

    [Fact]
    public async Task Submit_snapshot_rejects_forged_actor_role_and_submission_time_attribution()
    {
        for (var variant = 0; variant < 3; variant++)
        {
            var request = HumanInputResponseLifecycleTestData.Request(
                HumanInputResponsePolicyKind.ManualSelection,
                orderedRoleIds: ["selector-role"]);
            var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
            var command = HumanInputResponseLifecycleTestData.Submit(
                request,
                harness.Store.CurrentSnapshot!.Request.Head,
                $"hostile-submit-{variant}",
                $"hostile-response-{variant}");
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(command)).Status);
            var valid = harness.Store.CurrentSnapshot!;
            var operation = Assert.Single(valid.Operations);
            var artifact = Assert.Single(valid.Responses);
            var forgedArtifact = variant switch
            {
                0 => artifact with
                {
                    ActorId = HumanInputResponseLifecycleTestData.Actor("user-two"),
                    RespondentRoleId = "role-two",
                    ResponseHash = string.Empty,
                },
                1 => artifact with
                {
                    ActorId = HumanInputResponseLifecycleTestData.Actor("user-two"),
                    RespondentRoleId = "role-two",
                    ResponseHash = string.Empty,
                },
                _ => artifact with
                {
                    SubmittedAtUtc = artifact.SubmittedAtUtc.AddTicks(1),
                    ResponseHash = string.Empty,
                },
            };
            forgedArtifact = HumanInputResponseArtifactHash.Apply(forgedArtifact);
            var forgedReference = Reference(request, forgedArtifact);
            var forgedOperation = operation with
            {
                SubmittedResponse = forgedReference,
                ActorId = variant == 1 ? HumanInputResponseLifecycleTestData.Actor("user-two") : operation.ActorId,
                ActorRoleId = variant == 1 ? "role-one" : operation.ActorRoleId,
            };
            var hostile = new HumanInputResponseLifecycleStoreSnapshot(
                valid.Request,
                valid.ResponseRequest,
                [forgedArtifact],
                [forgedOperation],
                null);
            UseExactHostileRead(harness, hostile, forgedOperation);
            harness.Authenticator.Requests.Clear();

            var result = await harness.Service.MutateAsync(command);
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, result.Status);
            Assert.Empty(harness.Authenticator.Requests);
        }
    }

    [Fact]
    public async Task Eligibility_digest_rejects_stale_actor_role_time_and_authentication_substitutions()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var command = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "eligibility-digest-submit",
            "eligibility-digest-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(command)).Status);
        var evidence = Assert.Single(harness.Store.CurrentSnapshot!.Operations);

        Assert.True(HumanInputResponseEligibilityEvidenceHash.Matches(evidence));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(evidence with
        {
            ActorId = HumanInputResponseLifecycleTestData.Actor("user-two"),
        }));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(evidence with
        {
            ActorRoleId = "role-two",
        }));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(evidence with
        {
            RecordedAtUtc = evidence.RecordedAtUtc.AddTicks(1),
        }));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(evidence with
        {
            AuthenticationEvidenceHash = HumanInputResponseLifecycleTestData.Hash('b'),
        }));
        Assert.False(HumanInputResponseEligibilityEvidenceHash.Matches(null));
    }

    [Fact]
    public async Task Lifecycle_only_reads_reject_answer_operations_with_stale_eligibility_digests()
    {
        for (var variant = 0; variant < 4; variant++)
        {
            var request = HumanInputResponseLifecycleTestData.Request();
            var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
            Assert.Equal(
                HumanInputResponseLifecycleMutationStatus.Committed,
                (await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                    request,
                    harness.Store.CurrentSnapshot!.Request.Head,
                    $"answer-digest-{variant}",
                    $"answer-digest-response-{variant}"))).Status);
            var answered = harness.Store.CurrentSnapshot!.Request;
            var answer = Assert.IsType<HumanInputResponseOperationEvidence>(answered.AnswerOperation);
            var forged = variant switch
            {
                0 => answer with { ActorId = HumanInputResponseLifecycleTestData.Actor("user-two") },
                1 => answer with { ActorRoleId = "role-two" },
                2 => answer with { AuthenticationEvidenceHash = HumanInputResponseLifecycleTestData.Hash('b') },
                _ => answer with { RecordedAtUtc = answer.RecordedAtUtc.AddTicks(1) },
            };
            Assert.Contains(
                HumanInputResponseContractValidator.ValidateEvidence(forged).Errors,
                error => error.Code == HumanInputResponseValidationErrorCode.InvalidEligibilityEvidence);
            var hostile = new HumanInputRequestLifecycleStoreSnapshot(
                answered.Head,
                answered.RequestVersions,
                answered.Operations,
                forged);
            var command = HumanInputRequestLifecycleTestData.Command(
                HumanInputRequestLifecycleOperationKind.Cancel,
                $"lifecycle-answer-digest-{variant}",
                request.RequestId,
                null,
                expected: answer.PreviousHead,
                expectedBinding: request.Binding);
            HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness.LifecycleHarness);
            harness.LifecycleHarness.Store.ReadForMutationOverride = (_, _, _, _, _) => Task.FromResult(
                new HumanInputRequestLifecycleStoreReadResult(
                    HumanInputRequestLifecycleStoreReadStatus.Ready,
                    Math.Max(answered.Operations.Count, 1),
                    hostile,
                    null,
                    null));

            var result = await harness.LifecycleHarness.Service.MutateAsync(command);

            Assert.Equal(HumanInputRequestLifecycleMutationStatus.Ambiguous, result.Status);
            Assert.Empty(harness.LifecycleHarness.Authorizer.Requests);
            Assert.Empty(harness.LifecycleHarness.Store.Commits);
        }
    }

    [Fact]
    public async Task Command_aware_causality_rejects_false_late_malformed_and_duplicate_failures()
    {
        var lateRequest = HumanInputResponseLifecycleTestData.Request(
            expiresAtUtc: HumanInputResponseLifecycleTestData.Now.AddMinutes(1));
        var lateHarness = await HumanInputResponseLifecycleHarness.CreateAsync(lateRequest);
        lateHarness.Time.UtcNow = lateRequest.Timing.ExpiresAtUtc.AddTicks(1);
        var lateCommand = HumanInputResponseLifecycleTestData.Submit(
            lateRequest,
            lateHarness.Store.CurrentSnapshot!.Request.Head,
            "false-late-submit",
            "false-late-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Late, (await lateHarness.Service.MutateAsync(lateCommand)).Status);
        var lateSnapshot = lateHarness.Store.CurrentSnapshot!;
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(lateSnapshot.Operations), lateSnapshot));
        var falseLate = RehashEligibility(Assert.Single(lateSnapshot.Operations) with
        {
            RecordedAtUtc = lateRequest.Timing.ExpiresAtUtc,
        });
        var hostileLate = new HumanInputResponseLifecycleStoreSnapshot(
            lateSnapshot.Request,
            lateSnapshot.ResponseRequest,
            lateSnapshot.Responses,
            [falseLate],
            null);
        UseExactHostileRead(lateHarness, hostileLate, falseLate);
        Assert.False(HumanInputResponseOperationCausality.Matches(lateCommand, falseLate, hostileLate));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, (await lateHarness.Service.MutateAsync(lateCommand)).Status);

        var malformedRequest = HumanInputResponseLifecycleTestData.Request(maxTextCharacters: 3);
        var malformedHarness = await HumanInputResponseLifecycleHarness.CreateAsync(malformedRequest);
        var malformedCommand = HumanInputResponseLifecycleTestData.Submit(
            malformedRequest,
            malformedHarness.Store.CurrentSnapshot!.Request.Head,
            "false-malformed-submit",
            "false-malformed-response",
            HumanInputResponseLifecycleTestData.Text("too long"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Invalid, (await malformedHarness.Service.MutateAsync(malformedCommand)).Status);
        Assert.True(HumanInputResponseOperationCausality.Matches(
            Assert.Single(malformedHarness.Store.CurrentSnapshot!.Operations),
            malformedHarness.Store.CurrentSnapshot));
        var validCommand = HumanInputResponseLifecycleCommandHash.Apply(malformedCommand with
        {
            Value = HumanInputResponseLifecycleTestData.Text("ok"),
            CommandHash = string.Empty,
        });
        var malformedSnapshot = malformedHarness.Store.CurrentSnapshot!;
        var falseMalformed = RehashEligibility(Assert.Single(malformedSnapshot.Operations) with
        {
            CommandHash = validCommand.CommandHash,
        });
        var hostileMalformed = new HumanInputResponseLifecycleStoreSnapshot(
            malformedSnapshot.Request,
            malformedSnapshot.ResponseRequest,
            malformedSnapshot.Responses,
            [falseMalformed],
            null);
        UseExactHostileRead(malformedHarness, hostileMalformed, falseMalformed);
        Assert.False(HumanInputResponseOperationCausality.Matches(validCommand, falseMalformed, hostileMalformed));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, (await malformedHarness.Service.MutateAsync(validCommand)).Status);

        var duplicateRequest = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var duplicateHarness = await HumanInputResponseLifecycleHarness.CreateAsync(duplicateRequest);
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await duplicateHarness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                duplicateRequest,
                duplicateHarness.Store.CurrentSnapshot!.Request.Head,
                "duplicate-cause-first",
                "duplicate-cause-response"))).Status);
        duplicateHarness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var duplicateCommand = HumanInputResponseLifecycleTestData.Submit(
            duplicateRequest,
            duplicateHarness.Store.CurrentSnapshot!.Request.Head,
            "false-duplicate-submit",
            "duplicate-cause-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, (await duplicateHarness.Service.MutateAsync(duplicateCommand)).Status);
        var duplicateSnapshot = duplicateHarness.Store.CurrentSnapshot!;
        Assert.True(HumanInputResponseOperationCausality.Matches(duplicateSnapshot.Operations[^1], duplicateSnapshot));
        var falseDuplicate = duplicateSnapshot.Operations[^1];
        var hostileDuplicate = new HumanInputResponseLifecycleStoreSnapshot(
            duplicateSnapshot.Request,
            duplicateSnapshot.ResponseRequest,
            [],
            [falseDuplicate],
            null);
        UseExactHostileRead(duplicateHarness, hostileDuplicate, falseDuplicate);
        Assert.False(HumanInputResponseOperationCausality.Matches(duplicateCommand, falseDuplicate, hostileDuplicate));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, (await duplicateHarness.Service.MutateAsync(duplicateCommand)).Status);
    }

    [Fact]
    public async Task Inspected_structured_attempts_are_compared_by_value_and_bound_to_request_privacy()
    {
        var request = HumanInputResponseLifecycleTestData.Request();
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var structured = new HumanInputResponseValue(
            HumanInputResponseKind.Structured,
            null,
            null,
            null,
            ImmutableArray.Create(new HumanInputStructuredFieldValue("field-one", "private-structured-value", null)),
            null);
        var command = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "structured-attempt",
            "structured-attempt-response",
            structured);

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Invalid, (await harness.Service.MutateAsync(command)).Status);
        var snapshot = harness.Store.CurrentSnapshot!;
        var evidence = Assert.Single(snapshot.Operations);
        var attempt = Assert.IsType<HumanInputResponseArtifact>(evidence.AttemptedResponse);
        var equivalent = HumanInputResponseArtifactHash.Apply(attempt with
        {
            Value = attempt.Value with
            {
                StructuredFields = attempt.Value.StructuredFields!.Value.Select(field => field with { }).ToImmutableArray(),
            },
            ValueHash = string.Empty,
            ResponseHash = string.Empty,
        });
        var equivalentEvidence = evidence with { AttemptedResponse = equivalent };
        var equivalentSnapshot = new HumanInputResponseLifecycleStoreSnapshot(
            snapshot.Request,
            snapshot.ResponseRequest,
            snapshot.Responses,
            [equivalentEvidence],
            snapshot.Selection);

        Assert.True(HumanInputResponseOperationCausality.Matches(command, equivalentEvidence, equivalentSnapshot));
        Assert.True(HumanInputResponseOperationCausality.Matches(equivalentEvidence, equivalentSnapshot));

        var wrongPrivacy = HumanInputResponseArtifactHash.Apply(equivalent with
        {
            PrivacyClass = HumanInputPrivacyClass.Sensitive,
            ResponseHash = string.Empty,
        });
        var hostileEvidence = evidence with { AttemptedResponse = wrongPrivacy };
        var hostileSnapshot = new HumanInputResponseLifecycleStoreSnapshot(
            snapshot.Request,
            snapshot.ResponseRequest,
            snapshot.Responses,
            [hostileEvidence],
            snapshot.Selection);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(hostileEvidence).IsValid);
        Assert.False(HumanInputResponseOperationCausality.Matches(command, hostileEvidence, hostileSnapshot));
        Assert.False(HumanInputResponseOperationCausality.Matches(hostileEvidence, hostileSnapshot));
    }

    [Fact]
    public async Task Command_aware_causality_rejects_false_missing_withdrawn_and_selection_conflicts()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var missingHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var missingSubmit = HumanInputResponseLifecycleTestData.Submit(
            request,
            missingHarness.Store.CurrentSnapshot!.Request.Head,
            "missing-cause-submit",
            "missing-cause-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await missingHarness.Service.MutateAsync(missingSubmit)).Status);
        var existing = Reference(request, Assert.Single(missingHarness.Store.CurrentSnapshot!.Responses));
        var unknown = existing with
        {
            ResponseId = "missing-cause-unknown",
            ResponseHash = HumanInputResponseLifecycleTestData.Hash('b'),
        };
        var missingCommand = HumanInputResponseLifecycleTestData.Target(
            request,
            missingHarness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "false-response-not-found",
            unknown);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.NotFound, (await missingHarness.Service.MutateAsync(missingCommand)).Status);
        var validWithdraw = HumanInputResponseLifecycleCommandHash.Apply(missingCommand with
        {
            TargetResponses = [existing],
            CommandHash = string.Empty,
        });
        var missingSnapshot = missingHarness.Store.CurrentSnapshot!;
        var falseMissing = RehashEligibility(missingSnapshot.Operations[^1] with
        {
            CommandHash = validWithdraw.CommandHash,
            TargetResponses = [existing],
        });
        var hostileMissing = new HumanInputResponseLifecycleStoreSnapshot(
            missingSnapshot.Request,
            missingSnapshot.ResponseRequest,
            missingSnapshot.Responses,
            missingSnapshot.Operations.Take(missingSnapshot.Operations.Count - 1).Append(falseMissing).ToArray(),
            null);
        UseExactHostileRead(missingHarness, hostileMissing, falseMissing);
        Assert.False(HumanInputResponseOperationCausality.Matches(validWithdraw, falseMissing, hostileMissing));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, (await missingHarness.Service.MutateAsync(validWithdraw)).Status);

        var withdrawnHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var withdrawnSubmit = HumanInputResponseLifecycleTestData.Submit(
            request,
            withdrawnHarness.Store.CurrentSnapshot!.Request.Head,
            "withdrawn-cause-submit",
            "withdrawn-cause-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await withdrawnHarness.Service.MutateAsync(withdrawnSubmit)).Status);
        var withdrawnTarget = Reference(request, Assert.Single(withdrawnHarness.Store.CurrentSnapshot!.Responses));
        var firstWithdraw = HumanInputResponseLifecycleTestData.Target(
            request,
            withdrawnHarness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "withdrawn-cause-first",
            withdrawnTarget);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await withdrawnHarness.Service.MutateAsync(firstWithdraw)).Status);
        var alreadyCommand = HumanInputResponseLifecycleTestData.Target(
            request,
            withdrawnHarness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "false-already-withdrawn",
            withdrawnTarget);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, (await withdrawnHarness.Service.MutateAsync(alreadyCommand)).Status);
        var withdrawnSnapshot = withdrawnHarness.Store.CurrentSnapshot!;
        var falseWithdrawn = withdrawnSnapshot.Operations[^1];
        var hostileWithdrawn = new HumanInputResponseLifecycleStoreSnapshot(
            withdrawnSnapshot.Request,
            withdrawnSnapshot.ResponseRequest,
            withdrawnSnapshot.Responses,
            [withdrawnSnapshot.Operations[0], falseWithdrawn],
            null);
        UseExactHostileRead(withdrawnHarness, hostileWithdrawn, falseWithdrawn);
        Assert.False(HumanInputResponseOperationCausality.Matches(alreadyCommand, falseWithdrawn, hostileWithdrawn));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, (await withdrawnHarness.Service.MutateAsync(alreadyCommand)).Status);

        var selectionHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var selectionSubmit = HumanInputResponseLifecycleTestData.Submit(
            request,
            selectionHarness.Store.CurrentSnapshot!.Request.Head,
            "selection-cause-submit",
            "selection-cause-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await selectionHarness.Service.MutateAsync(selectionSubmit)).Status);
        var selectionTarget = Reference(request, Assert.Single(selectionHarness.Store.CurrentSnapshot!.Responses));
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await selectionHarness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
                request,
                selectionHarness.Store.CurrentSnapshot.Request.Head,
                HumanInputResponseOperationKind.Withdraw,
                "selection-cause-withdraw",
                selectionTarget))).Status);
        selectionHarness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var selectCommand = HumanInputResponseLifecycleTestData.Target(
            request,
            selectionHarness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Select,
            "false-selection-conflict",
            selectionTarget);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, (await selectionHarness.Service.MutateAsync(selectCommand)).Status);
        var selectionSnapshot = selectionHarness.Store.CurrentSnapshot!;
        var falseSelection = selectionSnapshot.Operations[^1];
        var hostileSelection = new HumanInputResponseLifecycleStoreSnapshot(
            selectionSnapshot.Request,
            selectionSnapshot.ResponseRequest,
            selectionSnapshot.Responses,
            [selectionSnapshot.Operations[0], falseSelection],
            null);
        UseExactHostileRead(selectionHarness, hostileSelection, falseSelection);
        Assert.False(HumanInputResponseOperationCausality.Matches(selectCommand, falseSelection, hostileSelection));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, (await selectionHarness.Service.MutateAsync(selectCommand)).Status);
    }

    [Fact]
    public async Task Manual_selection_snapshot_rejects_forged_selector_time_and_target_proof()
    {
        for (var variant = 0; variant < 3; variant++)
        {
            var selected = await CompleteManualSelectionAsync($"hostile-manual-{variant}");
            var valid = selected.Harness.Store.CurrentSnapshot!;
            var selection = valid.Selection!;
            var forgedSelection = variant switch
            {
                0 => selection with
                {
                    SelectorActorId = HumanInputResponseLifecycleTestData.Actor("user-one"),
                    SelectorRoleId = "role-one",
                    SelectionHash = string.Empty,
                },
                1 => selection with
                {
                    SelectedAtUtc = selection.SelectedAtUtc.AddTicks(1),
                    SelectionHash = string.Empty,
                },
                _ => selection with
                {
                    Responses = [Reference(selected.Harness.Request, valid.Responses[1])],
                    SelectionHash = string.Empty,
                },
            };
            forgedSelection = HumanInputResponseSelectionHash.Apply(forgedSelection);
            var selectionReference = HumanInputResponseSelectionReference.Create(forgedSelection);
            var operation = valid.Operations[^1];
            var resultHead = operation.ResultHead! with { AnswerSelection = selectionReference };
            var forgedOperation = operation with
            {
                ResultHead = resultHead,
                Selection = selectionReference,
            };
            var lifecycle = new HumanInputRequestLifecycleStoreSnapshot(
                resultHead,
                valid.Request.RequestVersions,
                valid.Request.Operations,
                forgedOperation);
            var hostile = new HumanInputResponseLifecycleStoreSnapshot(
                lifecycle,
                valid.ResponseRequest,
                valid.Responses,
                valid.Operations.Take(valid.Operations.Count - 1).Append(forgedOperation).ToArray(),
                forgedSelection);
            UseExactHostileRead(selected.Harness, hostile, forgedOperation);
            selected.Harness.Authenticator.Requests.Clear();

            var result = await selected.Harness.Service.MutateAsync(selected.Command);
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, result.Status);
            Assert.Empty(selected.Harness.Authenticator.Requests);
        }
    }

    [Fact]
    public async Task Private_response_values_and_attribution_never_escape_public_formatting_or_results()
    {
        const string PrivateValue = "private-value-never-format";
        const string PrivateExplanation = "private-explanation-never-format";
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var command = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "private-format-submit",
            "private-format-response",
            HumanInputResponseLifecycleTestData.Text(PrivateValue),
            PrivateExplanation);
        var result = await harness.Service.MutateAsync(command);
        var mutation = Assert.Single(harness.Store.Commits);
        var snapshot = harness.Store.CurrentSnapshot!;
        var authentication = new HumanInputResponseActorAuthentication(
            HumanInputResponseActorAuthenticationStatus.Authenticated,
            command.OperationId,
            command.CommandHash,
            "workspace-one",
            harness.Time.UtcNow,
            HumanInputResponseLifecycleTestData.Actor("user-one"),
            HumanInputResponseLifecycleTestData.Hash('e'));
        var formatted = new object?[]
        {
            command,
            result,
            mutation,
            mutation.Operation,
            mutation.ResponseToAppend,
            snapshot,
            snapshot.Selection,
            Assert.Single(harness.Authenticator.Requests),
            authentication,
        };

        Assert.All(formatted, value =>
        {
            var text = value?.ToString() ?? string.Empty;
            Assert.DoesNotContain(PrivateValue, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PrivateExplanation, text, StringComparison.Ordinal);
            Assert.DoesNotContain("user-one", text, StringComparison.Ordinal);
            Assert.DoesNotContain(HumanInputResponseLifecycleTestData.Hash('e'), text, StringComparison.Ordinal);
        });
        Assert.Null(result.Operation?.Selection);
        Assert.Equal(1, result.Projection!.AcceptedResponseCount);
    }

    private static async Task<(HumanInputResponseLifecycleHarness Harness, HumanInputResponseLifecycleCommand Command)> CompleteAutomaticPolicyAsync(
        HumanInputResponsePolicyKind policy)
    {
        var count = policy is HumanInputResponsePolicyKind.Quorum or HumanInputResponsePolicyKind.Merge ? 2 : (int?)null;
        string[]? roles = policy switch
        {
            HumanInputResponsePolicyKind.NamedRoles => ["role-two", "role-one"],
            HumanInputResponsePolicyKind.Merge => ["role-two", "role-one"],
            _ => null,
        };
        var request = HumanInputResponseLifecycleTestData.Request(policy, count, roles?.ToImmutableArray());
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var first = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            $"complete-{policy.ToString().ToLowerInvariant()}-one",
            "automatic-response-one",
            HumanInputResponseLifecycleTestData.Text(policy == HumanInputResponsePolicyKind.Quorum ? "same" : "one"));
        var firstResult = await harness.Service.MutateAsync(first);
        if (firstResult.Projection!.LifecycleStatus == HumanInputRequestLifecycleStatus.Answered)
        {
            return (harness, first);
        }
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var second = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            $"complete-{policy.ToString().ToLowerInvariant()}-two",
            "automatic-response-two",
            HumanInputResponseLifecycleTestData.Text(policy == HumanInputResponsePolicyKind.Quorum ? "same" : "two"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(second)).Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, harness.Store.CurrentSnapshot!.Request.Head.Status);
        return (harness, second);
    }

    private static async Task<(HumanInputResponseLifecycleHarness Harness, HumanInputResponseLifecycleCommand Command)> CompleteManualSelectionAsync(
        string suffix)
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                request,
                harness.Store.CurrentSnapshot!.Request.Head,
                $"{suffix}-submit-one",
                $"{suffix}-response-one"))).Status);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                request,
                harness.Store.CurrentSnapshot!.Request.Head,
                $"{suffix}-submit-two",
                $"{suffix}-response-two"))).Status);
        var target = Reference(request, harness.Store.CurrentSnapshot!.Responses[0]);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var select = HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Select,
            $"{suffix}-select",
            target);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(select)).Status);
        return (harness, select);
    }

    private static void UseExactHostileRead(
        HumanInputResponseLifecycleHarness harness,
        HumanInputResponseLifecycleStoreSnapshot snapshot,
        HumanInputResponseOperationEvidence evidence)
        => harness.Store.ReadForMutationOverride = (_, _, _, _) => Task.FromResult(
            new HumanInputResponseLifecycleStoreReadResult(
                HumanInputResponseLifecycleStoreReadStatus.Ready,
                Math.Max(snapshot.Operations.Count, 1),
                snapshot,
                new HumanInputResponseLifecycleStoredOperation(evidence.Request.RequestId, evidence)));

    private static HumanInputResponseReference Reference(HumanInputRequest request, HumanInputResponseArtifact response)
    {
        Assert.True(HumanInputResponseReference.TryCreate(request, response, out var reference, out var validation));
        Assert.True(validation.IsValid);
        return reference!;
    }

    private static HumanInputResponseOperationEvidence RehashEligibility(HumanInputResponseOperationEvidence evidence)
        => evidence with
        {
            EligibilityEvidenceHash = HumanInputResponseEligibilityEvidenceHash.Compute(
                evidence.ExpectedBinding.WorkspaceId,
                evidence.OperationId,
                evidence.CommandHash,
                evidence.Request,
                evidence.ActorId,
                evidence.ActorRoleId,
                evidence.AuthenticationEvidenceHash,
                evidence.RecordedAtUtc),
        };
}
