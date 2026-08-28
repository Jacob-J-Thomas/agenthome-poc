using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

public sealed class HumanInputResponseOperationCausalityChronologyTests
{
    [Fact]
    public async Task Claimed_submit_withdraw_and_select_history_matches_once_in_exact_durable_order()
    {
        var snapshot = await CompleteManualHistoryAsync("valid-chronology");

        Assert.Equal(
            [
                HumanInputResponseOperationKind.Submit,
                HumanInputResponseOperationKind.Submit,
                HumanInputResponseOperationKind.Withdraw,
                HumanInputResponseOperationKind.Select,
            ],
            snapshot.Operations.Select(operation => operation.Kind));
        Assert.All(snapshot.Operations, operation => Assert.True(HumanInputResponseOperationCausality.Matches(operation, snapshot)));
        Assert.True(HumanInputResponseOperationCausality.MatchesChronology(Observe(snapshot)));
    }

    [Fact]
    public async Task Equivalent_distinct_snapshot_aliases_are_accepted_but_divergent_aliases_are_rejected()
    {
        var snapshot = await CompleteManualHistoryAsync("snapshot-alias");
        var equivalent = snapshot.Operations
            .Select(operation => new HumanInputResponseOperationCausalityObservation(operation, Clone(snapshot)))
            .ToArray();

        Assert.True(HumanInputResponseOperationCausality.MatchesChronology(equivalent));

        var divergent = Clone(snapshot, operations: snapshot.Operations.Take(snapshot.Operations.Count - 1).ToArray());
        var observations = Observe(snapshot).ToArray();
        observations[^1] = new HumanInputResponseOperationCausalityObservation(snapshot.Operations[^1], divergent);

        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(observations));
    }

    [Fact]
    public async Task Middle_operation_tampering_reordering_removal_duplication_and_time_rollback_are_rejected()
    {
        var snapshot = await CompleteManualHistoryAsync("hostile-chronology");
        var valid = Observe(snapshot).ToArray();
        var tampered = RehashEligibility(snapshot.Operations[1] with
        {
            AuthenticationEvidenceHash = HumanInputResponseLifecycleTestData.Hash('b'),
        });
        var tamperedObservations = valid.ToArray();
        tamperedObservations[1] = new HumanInputResponseOperationCausalityObservation(tampered, snapshot);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(tampered).IsValid);
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(tamperedObservations));

        var reordered = valid.ToArray();
        (reordered[1], reordered[2]) = (reordered[2], reordered[1]);
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(reordered));

        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(valid.Where((_, index) => index != 1).ToArray()));
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(valid.Take(2).Append(valid[1]).Concat(valid.Skip(2)).ToArray()));

        var rolledBack = RehashEligibility(snapshot.Operations[2] with
        {
            RecordedAtUtc = snapshot.Operations[1].RecordedAtUtc.AddTicks(-1),
        });
        var rolledBackOperations = snapshot.Operations.ToArray();
        rolledBackOperations[2] = rolledBack;
        var rolledBackSnapshot = Clone(snapshot, operations: rolledBackOperations);
        Assert.True(HumanInputResponseContractValidator.ValidateEvidence(rolledBack).IsValid);
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(Observe(rolledBackSnapshot)));
    }

    [Fact]
    public async Task Wrong_response_request_artifact_reference_and_selection_snapshots_are_rejected()
    {
        var snapshot = await CompleteManualHistoryAsync("hostile-snapshot");
        var wrongRequest = snapshot.ResponseRequest with
        {
            RequestVersionId = "hostile-version",
            RequestHash = HumanInputResponseLifecycleTestData.Hash('b'),
        };
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(Observe(Clone(snapshot, responseRequest: wrongRequest))));

        var wrongReferenceOperations = snapshot.Operations.ToArray();
        wrongReferenceOperations[0] = wrongReferenceOperations[0] with
        {
            SubmittedResponse = wrongReferenceOperations[0].SubmittedResponse! with
            {
                ResponseId = "hostile-response-reference",
                ResponseHash = HumanInputResponseLifecycleTestData.Hash('c'),
            },
        };
        var wrongReference = Clone(snapshot, operations: wrongReferenceOperations);
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(Observe(wrongReference)));

        var wrongArtifacts = snapshot.Responses.ToArray();
        wrongArtifacts[0] = HumanInputResponseArtifactHash.Apply(wrongArtifacts[0] with
        {
            Value = HumanInputResponseLifecycleTestData.Text("hostile artifact value"),
            ValueHash = string.Empty,
            ResponseHash = string.Empty,
        });
        var wrongArtifact = Clone(snapshot, responses: wrongArtifacts);
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(Observe(wrongArtifact)));

        var selection = Assert.IsType<HumanInputResponseSelection>(snapshot.Selection);
        var wrongSelection = HumanInputResponseSelectionHash.Apply(selection with
        {
            SelectedAtUtc = selection.SelectedAtUtc.AddTicks(1),
            SelectionHash = string.Empty,
        });
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(Observe(Clone(snapshot, selection: wrongSelection))));
    }

    [Fact]
    public async Task Preclaim_stale_version_does_not_poison_later_exact_admission_and_fresh_operation()
    {
        var original = ManualRequest("preclaim-request", "preclaim-v0");
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(original);
        var candidate = HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
            original,
            "preclaim-v1",
            "Exact later candidate prompt.");
        var candidateReference = HumanInputResponseLifecycleTestData.Reference(candidate);
        var staleCommand = HumanInputResponseLifecycleTestData.Submit(
            original,
            harness.Store.CurrentSnapshot!.Request.Head,
            "preclaim-stale-operation",
            "preclaim-stale-response",
            expectedRequest: candidateReference);

        var stale = await harness.Service.MutateAsync(staleCommand);
        var historical = harness.Store.CurrentSnapshot!;
        Assert.Equal(HumanInputResponseOperationFailureCode.StaleResponse, stale.Operation!.FailureCode);
        Assert.Empty(historical.Operations);

        harness.LifecycleHarness.Time.Value = HumanInputResponseLifecycleTestData.Now.AddMinutes(6);
        var amend = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Amend,
            "preclaim-admit-exact-version",
            original.RequestId,
            candidate);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.LifecycleHarness.Service.MutateAsync(amend)).Status);
        harness.Store.ReplaceLifecycle(harness.LifecycleHarness.Store.Snapshot(original.RequestId)!);
        harness.Time.UtcNow = HumanInputResponseLifecycleTestData.Now.AddMinutes(7);
        var freshCommand = HumanInputResponseLifecycleTestData.Submit(
            candidate,
            harness.Store.CurrentSnapshot!.Request.Head,
            "preclaim-fresh-operation",
            "preclaim-fresh-response");
        var fresh = await harness.Service.MutateAsync(freshCommand);
        var current = harness.Store.CurrentSnapshot!;

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, fresh.Status);
        Assert.Equal(candidateReference, current.ResponseRequest);
        Assert.Single(current.Operations);
        Assert.True(HumanInputResponseOperationCausality.Matches(harness.Store.Commits[0].Operation, current));
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(current.Operations), current));
        Assert.True(HumanInputResponseOperationCausality.MatchesChronology(
        [
            new HumanInputResponseOperationCausalityObservation(harness.Store.Commits[0].Operation, current),
            new HumanInputResponseOperationCausalityObservation(Assert.Single(current.Operations), current),
        ]));
    }

    [Fact]
    public async Task Divergent_request_hashes_with_the_same_version_identity_do_not_alias()
    {
        var firstRequest = ManualRequest("non-alias-request", "shared-version");
        var secondRequest = HumanInputRequestHash.Apply(firstRequest with
        {
            Prompt = "A distinct immutable request with the same public version identity.",
            RequestHash = string.Empty,
        });
        var first = await HumanInputResponseLifecycleHarness.CreateAsync(firstRequest);
        var second = await HumanInputResponseLifecycleHarness.CreateAsync(secondRequest);
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await first.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                firstRequest,
                first.Store.CurrentSnapshot!.Request.Head,
                "non-alias-first-operation",
                "non-alias-first-response"))).Status);
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await second.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                secondRequest,
                second.Store.CurrentSnapshot!.Request.Head,
                "non-alias-second-operation",
                "non-alias-second-response"))).Status);
        var firstSnapshot = first.Store.CurrentSnapshot!;
        var secondSnapshot = second.Store.CurrentSnapshot!;

        Assert.Equal(firstSnapshot.ResponseRequest.RequestVersionId, secondSnapshot.ResponseRequest.RequestVersionId);
        Assert.NotEqual(firstSnapshot.ResponseRequest.RequestHash, secondSnapshot.ResponseRequest.RequestHash);
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(firstSnapshot.Operations), firstSnapshot));
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(secondSnapshot.Operations), secondSnapshot));
        Assert.True(HumanInputResponseOperationCausality.MatchesChronology(
        [
            new HumanInputResponseOperationCausalityObservation(Assert.Single(firstSnapshot.Operations), firstSnapshot),
            new HumanInputResponseOperationCausalityObservation(Assert.Single(secondSnapshot.Operations), secondSnapshot),
        ]));
    }

    [Fact]
    public async Task Request_not_found_before_later_creation_does_not_poison_the_fresh_exact_history()
    {
        var request = HumanInputResponseLifecycleTestData.Request(requestId: "later-request", requestVersionId: "later-v1");
        var store = new InMemoryHumanInputResponseLifecycleStore(null);
        var service = new HumanInputResponseLifecycleService(
            store,
            new RecordingHumanInputResponseActorAuthenticator(),
            new StubCapabilityAuthorityTransaction(),
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new MutableHumanInputResponseTimeProvider(HumanInputResponseLifecycleTestData.Now.AddMinutes(5)));
        var missingCommand = HumanInputResponseLifecycleTestData.Submit(
            request,
            PendingHead(request),
            "not-found-before-create",
            "not-found-response");
        var missing = await service.MutateAsync(missingCommand);
        Assert.Equal(HumanInputResponseOperationFailureCode.RequestNotFound, missing.Operation!.FailureCode);

        var lifecycle = new HumanInputRequestLifecycleHarness();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(lifecycle, request, "create-after-not-found");
        store.ReplaceLifecycle(lifecycle.Store.Snapshot(request.RequestId)!);
        var freshCommand = HumanInputResponseLifecycleTestData.Submit(
            request,
            store.CurrentSnapshot!.Request.Head,
            "fresh-after-create",
            "fresh-after-create-response");
        var fresh = await service.MutateAsync(freshCommand);
        var current = store.CurrentSnapshot!;

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, fresh.Status);
        Assert.True(HumanInputResponseOperationCausality.Matches(store.Commits[0].Operation, null));
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(current.Operations), current));
        Assert.True(HumanInputResponseOperationCausality.MatchesChronology(
        [
            new HumanInputResponseOperationCausalityObservation(store.Commits[0].Operation, null),
            new HumanInputResponseOperationCausalityObservation(Assert.Single(current.Operations), current),
        ]));
    }

    [Fact]
    public async Task Unclaimed_terminal_fallback_is_proved_by_lifecycle_state_without_advancing_exact_operation_context()
    {
        var request = ManualRequest("terminal-fallback-request", "terminal-fallback-v1");
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var submit = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "terminal-fallback-submit",
            "terminal-fallback-response"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, submit.Status);

        harness.LifecycleHarness.Time.Value = HumanInputResponseLifecycleTestData.Now.AddMinutes(6);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "terminal-fallback-cancel",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.LifecycleHarness.Service.MutateAsync(cancel)).Status);
        harness.Store.ReplaceLifecycle(harness.LifecycleHarness.Store.Snapshot(request.RequestId)!);
        var historicalTerminal = harness.Store.CurrentSnapshot!;
        var neverRetained = HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
            request,
            "terminal-fallback-future",
            "A future request version that was never admitted.");
        harness.Time.UtcNow = HumanInputResponseLifecycleTestData.Now.AddMinutes(7);
        var terminal = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            historicalTerminal.Request.Head,
            "terminal-fallback-attempt",
            "terminal-fallback-attempt-response",
            expectedRequest: HumanInputResponseLifecycleTestData.Reference(neverRetained)));

        Assert.Equal(HumanInputResponseOperationFailureCode.RequestTerminal, terminal.Operation!.FailureCode);
        Assert.Single(historicalTerminal.Operations);
        Assert.Single(harness.Store.CurrentSnapshot!.Operations);
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(historicalTerminal.Operations), historicalTerminal));
        Assert.True(HumanInputResponseOperationCausality.MatchesChronology(
        [
            new HumanInputResponseOperationCausalityObservation(Assert.Single(historicalTerminal.Operations), historicalTerminal),
            new HumanInputResponseOperationCausalityObservation(harness.Store.Commits[^1].Operation, historicalTerminal),
        ]));
    }

    [Fact]
    public async Task Null_malformed_and_over_bound_observation_batches_are_rejected_without_throwing()
    {
        var snapshot = await CompleteManualHistoryAsync("bounded-chronology");
        var evidence = snapshot.Operations[0];
        var valid = new HumanInputResponseOperationCausalityObservation(evidence, snapshot);

        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(null));
        Assert.True(HumanInputResponseOperationCausality.MatchesChronology([]));
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology([null!]));
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(
            [new HumanInputResponseOperationCausalityObservation(null!, snapshot)]));
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(
            [new HumanInputResponseOperationCausalityObservation(evidence, null)]));
        Assert.False(HumanInputResponseOperationCausality.MatchesChronology(
            Enumerable.Repeat(valid, HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore + 1).ToArray()));
    }

    private static async Task<HumanInputResponseLifecycleStoreSnapshot> CompleteManualHistoryAsync(string prefix)
    {
        var request = ManualRequest($"{prefix}-request", $"{prefix}-v1");
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var first = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            $"{prefix}-submit-one",
            $"{prefix}-response-one"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, first.Status);

        harness.Time.UtcNow = harness.Time.UtcNow.AddMinutes(1);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var second = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            $"{prefix}-submit-two",
            $"{prefix}-response-two"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, second.Status);

        harness.Time.UtcNow = harness.Time.UtcNow.AddMinutes(1);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-one");
        var firstReference = Reference(request, harness.Store.CurrentSnapshot!.Responses[0]);
        var withdrawn = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            $"{prefix}-withdraw-one",
            firstReference));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, withdrawn.Status);

        harness.Time.UtcNow = harness.Time.UtcNow.AddMinutes(1);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var secondReference = Reference(request, harness.Store.CurrentSnapshot!.Responses[1]);
        var selected = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Select,
            $"{prefix}-select-two",
            secondReference));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, selected.Status);
        return harness.Store.CurrentSnapshot!;
    }

    private static HumanInputRequest ManualRequest(string requestId, string requestVersionId)
        => HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"],
            requestId: requestId,
            requestVersionId: requestVersionId);

    private static IReadOnlyList<HumanInputResponseOperationCausalityObservation> Observe(
        HumanInputResponseLifecycleStoreSnapshot snapshot)
        => snapshot.Operations
            .Select(operation => new HumanInputResponseOperationCausalityObservation(operation, snapshot))
            .ToArray();

    private static HumanInputResponseLifecycleStoreSnapshot Clone(
        HumanInputResponseLifecycleStoreSnapshot source,
        HumanInputRequestReference? responseRequest = null,
        IReadOnlyList<HumanInputResponseArtifact>? responses = null,
        IReadOnlyList<HumanInputResponseOperationEvidence>? operations = null,
        HumanInputResponseSelection? selection = null)
        => new(
            new HumanInputRequestLifecycleStoreSnapshot(
                source.Request.Head with { CurrentRequest = source.Request.Head.CurrentRequest with { } },
                source.Request.RequestVersions.Select(request => request with { }).ToArray(),
                source.Request.Operations.Select(operation => operation with { }).ToArray(),
                source.Request.AnswerOperation is null ? null : source.Request.AnswerOperation with { }),
            responseRequest ?? source.ResponseRequest with { },
            responses ?? source.Responses.Select(response => response with { }).ToArray(),
            operations ?? source.Operations.Select(operation => operation with { }).ToArray(),
            selection ?? source.Selection);

    private static HumanInputResponseReference Reference(HumanInputRequest request, HumanInputResponseArtifact response)
    {
        Assert.True(HumanInputResponseReference.TryCreate(request, response, out var reference, out var validation));
        Assert.True(validation.IsValid);
        return reference!;
    }

    private static HumanInputRequestLifecycleHead PendingHead(HumanInputRequest request)
        => new(
            HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
            request.RequestId,
            1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputResponseLifecycleTestData.Reference(request),
            0,
            null,
            null,
            "expected-create-operation",
            HumanInputResponseLifecycleTestData.Now);

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
