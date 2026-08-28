using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.Capabilities;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

public sealed class HumanInputResponseLifecycleServiceTests
{
    [Fact]
    public async Task First_valid_submission_answers_once_and_replays_for_the_same_reauthenticated_actor()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var command = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "submit-first-valid",
            "response-one",
            HumanInputResponseLifecycleTestData.Text("private accepted value"),
            "private explanation");

        var committed = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, committed.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, committed.Projection!.LifecycleStatus);
        Assert.NotNull(committed.Operation!.Selection);
        Assert.Equal(1, committed.Projection.AcceptedResponseCount);
        Assert.False(harness.Store.LastCommitTokenCanBeCanceled);
        Assert.DoesNotContain("private accepted value", committed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private explanation", committed.ToString(), StringComparison.Ordinal);
        Assert.True(HumanInputResponseOperationCausality.Matches(
            Assert.Single(harness.Store.CurrentSnapshot!.Operations),
            harness.Store.CurrentSnapshot));

        harness.Time.UtcNow = harness.Request.Timing.ExpiresAtUtc.AddDays(1);
        var replayed = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, replayed.Status);
        Assert.Equal(2, harness.Authenticator.Requests.Count);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var changedActor = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Denied, changedActor.Status);
        Assert.Null(changedActor.Operation);
    }

    [Fact]
    public async Task Manual_response_can_be_withdrawn_by_its_owner_or_selected_by_an_admitted_selector()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var selectionHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var submit = HumanInputResponseLifecycleTestData.Submit(
            request,
            selectionHarness.Store.CurrentSnapshot!.Request.Head,
            "manual-submit",
            "manual-response");

        var pending = await selectionHarness.Service.MutateAsync(submit);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, pending.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, pending.Projection!.LifecycleStatus);
        Assert.Null(pending.Operation!.Selection);
        var target = Reference(request, Assert.Single(selectionHarness.Store.CurrentSnapshot!.Responses));

        selectionHarness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var select = HumanInputResponseLifecycleTestData.Target(
            request,
            selectionHarness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Select,
            "manual-select",
            target);
        var selected = await selectionHarness.Service.MutateAsync(select);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, selected.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, selected.Projection!.LifecycleStatus);
        Assert.Equal("selector-one", selectionHarness.Store.CurrentSnapshot!.Selection!.SelectorActorId!.Value);
        Assert.Equal("selector-role", selectionHarness.Store.CurrentSnapshot.Selection.SelectorRoleId);
        Assert.True(HumanInputResponseOperationCausality.Matches(
            selectionHarness.Store.CurrentSnapshot.Operations[^1],
            selectionHarness.Store.CurrentSnapshot));

        var withdrawHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var withdrawSubmit = HumanInputResponseLifecycleTestData.Submit(
            request,
            withdrawHarness.Store.CurrentSnapshot!.Request.Head,
            "withdraw-submit",
            "withdraw-response");
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await withdrawHarness.Service.MutateAsync(withdrawSubmit)).Status);
        var withdrawTarget = Reference(request, Assert.Single(withdrawHarness.Store.CurrentSnapshot!.Responses));
        withdrawHarness.Time.UtcNow = request.Timing.ExpiresAtUtc.AddDays(1);
        var withdraw = HumanInputResponseLifecycleTestData.Target(
            request,
            withdrawHarness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "withdraw-own-response",
            withdrawTarget);
        var withdrawn = await withdrawHarness.Service.MutateAsync(withdraw);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, withdrawn.Status);
        Assert.Equal(0, withdrawn.Projection!.ActiveResponseCount);
        Assert.Equal(1, withdrawn.Projection.WithdrawnResponseCount);
        Assert.True(HumanInputResponseOperationCausality.Matches(
            withdrawHarness.Store.CurrentSnapshot!.Operations[^1],
            withdrawHarness.Store.CurrentSnapshot));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, (await withdrawHarness.Service.MutateAsync(withdraw)).Status);
    }

    [Fact]
    public async Task Automatic_policies_select_the_exact_durable_response_order()
    {
        await AssertAutomaticPolicyAsync(
            HumanInputResponsePolicyKind.Quorum,
            requiredResponseCount: 2,
            orderedRoleIds: null,
            firstValue: "agreement",
            secondValue: "agreement",
            expectedResponseIds: ["response-one", "response-two"]);
        await AssertAutomaticPolicyAsync(
            HumanInputResponsePolicyKind.NamedRoles,
            requiredResponseCount: null,
            orderedRoleIds: ["role-two", "role-one"],
            firstValue: "first",
            secondValue: "second",
            expectedResponseIds: ["response-two", "response-one"]);
        await AssertAutomaticPolicyAsync(
            HumanInputResponsePolicyKind.Merge,
            requiredResponseCount: 2,
            orderedRoleIds: ["role-two", "role-one", "selector-role"],
            firstValue: "first",
            secondValue: "second",
            expectedResponseIds: ["response-two", "response-one"]);
    }

    [Fact]
    public async Task Eligibility_precedes_expiry_shape_and_target_disclosure_and_is_durably_proved()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            expiresAtUtc: HumanInputResponseLifecycleTestData.Now.AddMinutes(1),
            maxTextCharacters: 3);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        harness.Time.UtcNow = request.Timing.ExpiresAtUtc.AddMinutes(1);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("outsider");
        var malformedAndLate = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "outsider-submit",
            "outsider-response",
            HumanInputResponseLifecycleTestData.Text("too long"));

        var ineligible = await harness.Service.MutateAsync(malformedAndLate);

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ineligible, ineligible.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.IneligibleRespondent, ineligible.Operation!.FailureCode);
        Assert.Null(Assert.Single(harness.Store.CurrentSnapshot!.Operations).ActorRoleId);
        Assert.True(HumanInputResponseOperationCausality.Matches(
            Assert.Single(harness.Store.CurrentSnapshot.Operations),
            harness.Store.CurrentSnapshot));

        var manual = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"],
            expiresAtUtc: HumanInputResponseLifecycleTestData.Now.AddMinutes(1));
        var selectorHarness = await HumanInputResponseLifecycleHarness.CreateAsync(manual);
        selectorHarness.Time.UtcNow = manual.Timing.ExpiresAtUtc.AddMinutes(1);
        var unknownTarget = new HumanInputResponseReference(
            1,
            "unknown-response",
            HumanInputResponseLifecycleTestData.Reference(manual),
            HumanInputResponseLifecycleTestData.Hash('b'),
            HumanInputResponseLifecycleTestData.Hash('c'));
        var select = HumanInputResponseLifecycleTestData.Target(
            manual,
            selectorHarness.Store.CurrentSnapshot!.Request.Head,
            HumanInputResponseOperationKind.Select,
            "ineligible-selector",
            unknownTarget);
        var selectorResult = await selectorHarness.Service.MutateAsync(select);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ineligible, selectorResult.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.IneligibleSelector, selectorResult.Operation!.FailureCode);
    }

    [Fact]
    public async Task Eligible_non_owner_withdrawal_is_proved_but_forged_owner_ineligibility_is_rejected()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var submit = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "owner-submit",
            "owned-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(submit)).Status);
        var target = Reference(request, Assert.Single(harness.Store.CurrentSnapshot!.Responses));
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var withdraw = HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "wrong-owner-withdraw",
            target);

        var denied = await harness.Service.MutateAsync(withdraw);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ineligible, denied.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.IneligibleRespondent, denied.Operation!.FailureCode);

        var validSnapshot = harness.Store.CurrentSnapshot!;
        var forgedEvidence = validSnapshot.Operations[^1] with
        {
            ActorId = HumanInputResponseLifecycleTestData.Actor("user-one"),
        };
        var forgedSnapshot = new HumanInputResponseLifecycleStoreSnapshot(
            validSnapshot.Request,
            validSnapshot.ResponseRequest,
            validSnapshot.Responses,
            validSnapshot.Operations.Take(validSnapshot.Operations.Count - 1).Append(forgedEvidence).ToArray(),
            validSnapshot.Selection);
        harness.Store.ReadForMutationOverride = (_, _, _, _) => Task.FromResult(
            new HumanInputResponseLifecycleStoreReadResult(
                HumanInputResponseLifecycleStoreReadStatus.Ready,
                forgedSnapshot.Operations.Count,
                forgedSnapshot,
                new HumanInputResponseLifecycleStoredOperation(request.RequestId, forgedEvidence)));
        harness.Authenticator.Requests.Clear();

        var forged = await harness.Service.MutateAsync(withdraw);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ambiguous, forged.Status);
        Assert.Empty(harness.Authenticator.Requests);
    }

    [Fact]
    public async Task Optimistic_conflicts_reauthenticate_and_post_intent_crashes_recover_exactly()
    {
        var retryHarness = await HumanInputResponseLifecycleHarness.CreateAsync();
        retryHarness.Store.ConflictsRemaining = 1;
        var retryCommand = HumanInputResponseLifecycleTestData.Submit(
            retryHarness.Request,
            retryHarness.Store.CurrentSnapshot!.Request.Head,
            "retry-submit",
            "retry-response");
        var retried = await retryHarness.Service.MutateAsync(retryCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, retried.Status);
        Assert.Equal(2, retryHarness.Authenticator.Requests.Count);
        Assert.Equal(2, retryHarness.Store.Commits.Count);

        var recoveryHarness = await HumanInputResponseLifecycleHarness.CreateAsync();
        recoveryHarness.Store.ThrowAfterCommit = true;
        var recoveryCommand = HumanInputResponseLifecycleTestData.Submit(
            recoveryHarness.Request,
            recoveryHarness.Store.CurrentSnapshot!.Request.Head,
            "recover-submit",
            "recover-response");
        var recovered = await recoveryHarness.Service.MutateAsync(recoveryCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, recovered.Status);
        Assert.NotNull(recovered.Operation);
        Assert.Single(recoveryHarness.Store.CurrentSnapshot!.Responses);
    }

    [Fact]
    public async Task Authentication_echo_corruption_and_pre_intent_cancellation_fail_closed()
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync();
        var command = HumanInputResponseLifecycleTestData.Submit(
            harness.Request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "bad-auth-echo",
            "response-one");
        harness.Authenticator.Override = request => new HumanInputResponseActorAuthentication(
            HumanInputResponseActorAuthenticationStatus.Authenticated,
            "different-operation",
            request.CommandHash,
            request.WorkspaceId,
            request.EvaluatedAtUtc,
            HumanInputResponseLifecycleTestData.Actor("user-one"),
            HumanInputResponseLifecycleTestData.Hash('a'));
        var unavailable = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Unavailable, unavailable.Status);
        Assert.Empty(harness.Store.Commits);

        harness.Authenticator.Override = null;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.Service.MutateAsync(
            HumanInputResponseLifecycleTestData.Submit(
                harness.Request,
                harness.Store.CurrentSnapshot!.Request.Head,
                "cancel-before-intent",
                "cancelled-response"),
            cancellation.Token));
        Assert.Empty(harness.Store.Commits);
    }

    [Fact]
    public async Task Missing_requests_and_stale_or_terminal_heads_have_explicit_precedence()
    {
        var request = HumanInputResponseLifecycleTestData.Request();
        var head = PendingHead(request);
        var missingStore = new InMemoryHumanInputResponseLifecycleStore(null);
        var missing = new HumanInputResponseLifecycleService(
            missingStore,
            new RecordingHumanInputResponseActorAuthenticator(),
            new StubCapabilityAuthorityTransaction(),
            "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            new MutableHumanInputResponseTimeProvider(HumanInputResponseLifecycleTestData.Now.AddMinutes(5)));
        var missingCommand = HumanInputResponseLifecycleTestData.Submit(
            request,
            head,
            "missing-request-submit",
            "missing-response");
        var missingResult = await missing.MutateAsync(missingCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.NotFound, missingResult.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.RequestNotFound, missingResult.Operation!.FailureCode);
        Assert.True(HumanInputResponseOperationCausality.Matches(Assert.Single(missingStore.Commits).Operation, null));
        var laterLifecycle = new HumanInputRequestLifecycleHarness();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(laterLifecycle, request);
        missingStore.ReplaceLifecycle(laterLifecycle.Store.Snapshot(request.RequestId)!);
        var missingReplay = await missing.MutateAsync(missingCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, missingReplay.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.RequestNotFound, missingReplay.Operation!.FailureCode);
        Assert.Null(missingReplay.Projection);
        var freshAfterCreate = HumanInputResponseLifecycleTestData.Submit(
            request,
            missingStore.CurrentSnapshot!.Request.Head,
            "fresh-submit-after-create",
            "fresh-response-after-create");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await missing.MutateAsync(freshAfterCreate)).Status);
        var originalAfterFresh = await missing.MutateAsync(missingCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, originalAfterFresh.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.RequestNotFound, originalAfterFresh.Operation!.FailureCode);
        Assert.Null(originalAfterFresh.Projection);

        var staleHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var staleReference = staleHarness.Store.CurrentSnapshot!.Request.Head.CurrentRequest with
        {
            RequestVersionId = "stale-version",
            RequestHash = HumanInputResponseLifecycleTestData.Hash('b'),
        };
        var staleCommand = HumanInputResponseLifecycleTestData.Submit(
            request,
            staleHarness.Store.CurrentSnapshot.Request.Head,
            "stale-submit",
            "stale-response",
            expectedRequest: staleReference);
        var stale = await staleHarness.Service.MutateAsync(staleCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, stale.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.StaleResponse, stale.Operation!.FailureCode);

        var terminalHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            terminalHarness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "cancel-before-response",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await terminalHarness.LifecycleHarness.Service.MutateAsync(cancel)).Status);
        var cancelledLifecycle = terminalHarness.LifecycleHarness.Store.Snapshot(request.RequestId)!;
        terminalHarness.Store.ReplaceLifecycle(cancelledLifecycle);
        var cancelledHead = cancelledLifecycle.Head;
        terminalHarness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("outsider");
        var terminalCommand = HumanInputResponseLifecycleTestData.Submit(
            request,
            cancelledHead,
            "terminal-submit",
            "terminal-response");
        var terminal = await terminalHarness.Service.MutateAsync(terminalCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, terminal.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.RequestTerminal, terminal.Operation!.FailureCode);
    }

    [Theory]
    [InlineData(HumanInputResponsePolicyKind.FirstValid)]
    [InlineData(HumanInputResponsePolicyKind.Quorum)]
    [InlineData(HumanInputResponsePolicyKind.NamedRoles)]
    [InlineData(HumanInputResponsePolicyKind.Merge)]
    [InlineData(HumanInputResponsePolicyKind.ManualSelection)]
    public async Task Terminal_precedence_is_independent_of_response_policy(HumanInputResponsePolicyKind policy)
    {
        var request = policy switch
        {
            HumanInputResponsePolicyKind.Quorum => HumanInputResponseLifecycleTestData.Request(policy, requiredResponseCount: 2),
            HumanInputResponsePolicyKind.NamedRoles => HumanInputResponseLifecycleTestData.Request(policy, orderedRoleIds: ["role-one"]),
            HumanInputResponsePolicyKind.Merge => HumanInputResponseLifecycleTestData.Request(policy, requiredResponseCount: 1, orderedRoleIds: ["role-one"]),
            HumanInputResponsePolicyKind.ManualSelection => HumanInputResponseLifecycleTestData.Request(policy, orderedRoleIds: ["selector-role"]),
            _ => HumanInputResponseLifecycleTestData.Request(policy),
        };
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            $"terminal-{policy.ToString().ToLowerInvariant()}-cancel",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await harness.LifecycleHarness.Service.MutateAsync(cancel)).Status);
        var terminalLifecycle = harness.LifecycleHarness.Store.Snapshot(request.RequestId)!;
        harness.Store.ReplaceLifecycle(terminalLifecycle);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("outsider");

        var submit = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            terminalLifecycle.Head,
            $"terminal-{policy.ToString().ToLowerInvariant()}-submit",
            $"terminal-{policy.ToString().ToLowerInvariant()}-response"));

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, submit.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.RequestTerminal, submit.Operation!.FailureCode);

        if (policy == HumanInputResponsePolicyKind.ManualSelection)
        {
            var unknownTarget = new HumanInputResponseReference(
                HumanInputResponseReference.CurrentSchemaVersion,
                "terminal-unknown-response",
                HumanInputResponseLifecycleTestData.Reference(request),
                HumanInputResponseLifecycleTestData.Hash('b'),
                HumanInputResponseLifecycleTestData.Hash('c'));
            var select = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
                request,
                terminalLifecycle.Head,
                HumanInputResponseOperationKind.Select,
                "terminal-manual-select",
                unknownTarget));
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, select.Status);
            Assert.Equal(HumanInputResponseOperationFailureCode.RequestTerminal, select.Operation!.FailureCode);
        }
    }

    private static async Task AssertAutomaticPolicyAsync(
        HumanInputResponsePolicyKind policy,
        int? requiredResponseCount,
        ImmutableArray<string>? orderedRoleIds,
        string firstValue,
        string secondValue,
        string[] expectedResponseIds)
    {
        var request = HumanInputResponseLifecycleTestData.Request(policy, requiredResponseCount, orderedRoleIds);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var first = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            $"{policy.ToString().ToLowerInvariant()}-one",
            "response-one",
            HumanInputResponseLifecycleTestData.Text(firstValue));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(first)).Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Pending, harness.Store.CurrentSnapshot!.Request.Head.Status);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var second = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            $"{policy.ToString().ToLowerInvariant()}-two",
            "response-two",
            HumanInputResponseLifecycleTestData.Text(secondValue));
        var answered = await harness.Service.MutateAsync(second);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, answered.Status);
        Assert.Equal(HumanInputRequestLifecycleStatus.Answered, answered.Projection!.LifecycleStatus);
        Assert.Equal(expectedResponseIds, harness.Store.CurrentSnapshot!.Selection!.Responses.Select(response => response.ResponseId));
        Assert.True(HumanInputResponseOperationCausality.Matches(
            harness.Store.CurrentSnapshot.Operations[^1],
            harness.Store.CurrentSnapshot));
    }

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
            "create-request",
            HumanInputResponseLifecycleTestData.Now);
}
