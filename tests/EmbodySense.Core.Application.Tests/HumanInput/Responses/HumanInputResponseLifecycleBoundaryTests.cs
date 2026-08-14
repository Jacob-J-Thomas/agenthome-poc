using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

public sealed class HumanInputResponseLifecycleBoundaryTests
{
    [Fact]
    public async Task Submit_and_select_accept_the_exact_expiry_endpoint_and_reject_later_instants()
    {
        var submitRequest = HumanInputResponseLifecycleTestData.Request(
            expiresAtUtc: HumanInputResponseLifecycleTestData.Now.AddMinutes(10));
        var exactSubmit = await HumanInputResponseLifecycleHarness.CreateAsync(submitRequest);
        exactSubmit.Time.UtcNow = submitRequest.Timing.ExpiresAtUtc;
        var submit = HumanInputResponseLifecycleTestData.Submit(
            submitRequest,
            exactSubmit.Store.CurrentSnapshot!.Request.Head,
            "submit-at-endpoint",
            "endpoint-response");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await exactSubmit.Service.MutateAsync(submit)).Status);

        var lateSubmit = await HumanInputResponseLifecycleHarness.CreateAsync(submitRequest);
        lateSubmit.Time.UtcNow = submitRequest.Timing.ExpiresAtUtc.AddTicks(1);
        var late = await lateSubmit.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            submitRequest,
            lateSubmit.Store.CurrentSnapshot!.Request.Head,
            "submit-after-endpoint",
            "late-response"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Late, late.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.LateResponse, late.Operation!.FailureCode);

        var manualRequest = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"],
            expiresAtUtc: HumanInputResponseLifecycleTestData.Now.AddMinutes(10));
        var exactSelect = await SeedManualResponseAsync(manualRequest, "exact-select");
        exactSelect.Harness.Time.UtcNow = manualRequest.Timing.ExpiresAtUtc;
        exactSelect.Harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var select = HumanInputResponseLifecycleTestData.Target(
            manualRequest,
            exactSelect.Harness.Store.CurrentSnapshot!.Request.Head,
            HumanInputResponseOperationKind.Select,
            "select-at-endpoint",
            exactSelect.Reference);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await exactSelect.Harness.Service.MutateAsync(select)).Status);

        var lateSelect = await SeedManualResponseAsync(manualRequest, "late-select");
        lateSelect.Harness.Time.UtcNow = manualRequest.Timing.ExpiresAtUtc.AddTicks(1);
        lateSelect.Harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var lateSelection = await lateSelect.Harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
            manualRequest,
            lateSelect.Harness.Store.CurrentSnapshot!.Request.Head,
            HumanInputResponseOperationKind.Select,
            "select-after-endpoint",
            lateSelect.Reference));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Late, lateSelection.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.LateResponse, lateSelection.Operation!.FailureCode);
    }

    [Fact]
    public async Task Duplicate_response_identity_and_duplicate_active_actor_are_explicit_conflicts()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var head = harness.Store.CurrentSnapshot!.Request.Head;
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                request,
                head,
                "first-submit",
                "same-response"))).Status);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var duplicateId = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            head,
            "duplicate-id-submit",
            "same-response"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, duplicateId.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.DuplicateResponse, duplicateId.Operation!.FailureCode);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-one");
        var duplicateActor = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            head,
            "duplicate-actor-submit",
            "different-response"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, duplicateActor.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.DuplicateResponse, duplicateActor.Operation!.FailureCode);
    }

    [Fact]
    public async Task Missing_withdrawal_withdrawn_replay_and_withdrawn_selection_are_distinct()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var seeded = await SeedManualResponseAsync(request, "target-outcomes");
        var harness = seeded.Harness;
        var unknown = seeded.Reference with
        {
            ResponseId = "unknown-response",
            ResponseHash = HumanInputResponseLifecycleTestData.Hash('b'),
        };
        var missing = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "withdraw-missing",
            unknown));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.NotFound, missing.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.ResponseNotFound, missing.Operation!.FailureCode);

        var withdraw = HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Withdraw,
            "withdraw-retained",
            seeded.Reference);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(withdraw)).Status);
        var alreadyCommand = EmbodySense.Core.Application.HumanInput.Responses.HumanInputResponseLifecycleCommandHash.Apply(
            withdraw with
            {
                OperationId = "withdraw-already",
                CommandHash = string.Empty,
            });
        var already = await harness.Service.MutateAsync(alreadyCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, already.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.ResponseAlreadyWithdrawn, already.Operation!.FailureCode);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var nonOwnerCommand = EmbodySense.Core.Application.HumanInput.Responses.HumanInputResponseLifecycleCommandHash.Apply(
            withdraw with
            {
                OperationId = "withdraw-already-non-owner",
                CommandHash = string.Empty,
            });
        var nonOwner = await harness.Service.MutateAsync(nonOwnerCommand);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ineligible, nonOwner.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.IneligibleRespondent, nonOwner.Operation!.FailureCode);
        Assert.Null(harness.Store.CurrentSnapshot!.Operations[^1].ActorRoleId);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Replayed, (await harness.Service.MutateAsync(nonOwnerCommand)).Status);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("selector-one");
        var selection = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
            request,
            harness.Store.CurrentSnapshot.Request.Head,
            HumanInputResponseOperationKind.Select,
            "select-withdrawn",
            seeded.Reference));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, selection.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.SelectionConflict, selection.Operation!.FailureCode);
    }

    [Fact]
    public async Task Malformed_responses_and_finite_response_and_operation_bounds_are_durable_or_explicit()
    {
        var malformedRequest = HumanInputResponseLifecycleTestData.Request(maxTextCharacters: 3);
        var malformedHarness = await HumanInputResponseLifecycleHarness.CreateAsync(malformedRequest);
        var malformed = await malformedHarness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            malformedRequest,
            malformedHarness.Store.CurrentSnapshot!.Request.Head,
            "malformed-submit",
            "malformed-response",
            HumanInputResponseLifecycleTestData.Text("too long")));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Invalid, malformed.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.MalformedResponse, malformed.Operation!.FailureCode);
        Assert.Empty(malformedHarness.Store.CurrentSnapshot!.Responses);

        var boundedRequest = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var responseLimit = await HumanInputResponseLifecycleHarness.CreateAsync(boundedRequest);
        var boundedResponses = new List<HumanInputResponseArtifact>(HumanInputResponseContractLimits.MaxResponsesPerRequest);
        var boundedResponseOperations = new List<HumanInputResponseOperationEvidence>(HumanInputResponseContractLimits.MaxResponsesPerRequest * 2);
        for (var index = 0; index < HumanInputResponseContractLimits.MaxResponsesPerRequest; index++)
        {
            var isolated = responseLimit.CreateIsolatedResponseStore();
            var responseId = $"bounded-response-{index}";
            var submit = await isolated.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                boundedRequest,
                isolated.Store.CurrentSnapshot!.Request.Head,
                $"bounded-submit-{index}",
                responseId));
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, submit.Status);
            var reference = Reference(boundedRequest, isolated.Store.CurrentSnapshot!.Responses[^1]);
            var withdraw = await isolated.Service.MutateAsync(HumanInputResponseLifecycleTestData.Target(
                boundedRequest,
                isolated.Store.CurrentSnapshot.Request.Head,
                HumanInputResponseOperationKind.Withdraw,
                $"bounded-withdraw-{index}",
                reference));
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, withdraw.Status);
            boundedResponses.AddRange(isolated.Store.CurrentSnapshot.Responses);
            boundedResponseOperations.AddRange(isolated.Store.CurrentSnapshot.Operations);
        }
        responseLimit.Store.SeedCurrentSnapshot(new HumanInputResponseLifecycleStoreSnapshot(
            responseLimit.Store.CurrentSnapshot!.Request,
            HumanInputResponseLifecycleTestData.Reference(boundedRequest),
            boundedResponses,
            boundedResponseOperations,
            null));
        var exhaustedResponses = await responseLimit.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            boundedRequest,
            responseLimit.Store.CurrentSnapshot!.Request.Head,
            "bounded-submit-exhausted",
            "bounded-response-exhausted"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.LimitExceeded, exhaustedResponses.Status);
        Assert.Equal(HumanInputResponseOperationFailureCode.ResponseLimitExceeded, exhaustedResponses.Operation!.FailureCode);

        var operationRequest = HumanInputResponseLifecycleTestData.Request(maxTextCharacters: 1);
        var operationLimit = await HumanInputResponseLifecycleHarness.CreateAsync(operationRequest);
        var boundedOperations = new List<HumanInputResponseOperationEvidence>(HumanInputResponseContractLimits.MaxOperationsPerRequest);
        for (var index = 0; index < HumanInputResponseContractLimits.MaxOperationsPerRequest; index++)
        {
            var isolated = operationLimit.CreateIsolatedResponseStore();
            var rejected = await isolated.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                operationRequest,
                isolated.Store.CurrentSnapshot!.Request.Head,
                $"malformed-operation-{index}",
                $"malformed-response-{index}",
                HumanInputResponseLifecycleTestData.Text("xx")));
            Assert.Equal(HumanInputResponseLifecycleMutationStatus.Invalid, rejected.Status);
            boundedOperations.AddRange(isolated.Store.CurrentSnapshot!.Operations);
        }
        operationLimit.Store.SeedCurrentSnapshot(new HumanInputResponseLifecycleStoreSnapshot(
            operationLimit.Store.CurrentSnapshot!.Request,
            HumanInputResponseLifecycleTestData.Reference(operationRequest),
            [],
            boundedOperations,
            null));
        var exhaustedOperations = await operationLimit.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            operationRequest,
            operationLimit.Store.CurrentSnapshot!.Request.Head,
            "malformed-operation-exhausted",
            "malformed-response-exhausted",
            HumanInputResponseLifecycleTestData.Text("xx")));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.LimitExceeded, exhaustedOperations.Status);
        Assert.Null(exhaustedOperations.Operation);
    }

    [Fact]
    public async Task Trusted_clock_rollback_behind_the_last_response_operation_creates_no_durable_intent()
    {
        var request = HumanInputResponseLifecycleTestData.Request(
            HumanInputResponsePolicyKind.ManualSelection,
            orderedRoleIds: ["selector-role"]);
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var firstTime = harness.Time.UtcNow;
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                request,
                harness.Store.CurrentSnapshot!.Request.Head,
                "clock-first-submit",
                "clock-first-response"))).Status);
        harness.Time.UtcNow = firstTime.AddTicks(-1);
        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("outsider");
        var ineligible = await harness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "clock-rollback-ineligible",
            "clock-rollback-ineligible-response"));
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Ineligible, ineligible.Status);
        Assert.Null(ineligible.Operation);
        Assert.Single(harness.Store.Commits);
        Assert.Single(harness.Store.CurrentSnapshot!.Operations);

        harness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        var command = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            "clock-rollback-submit",
            "clock-rollback-response");

        var unavailable = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Operation);
        Assert.Single(harness.Store.Commits);
        Assert.Single(harness.Store.CurrentSnapshot!.Operations);

        harness.Time.UtcNow = firstTime.AddTicks(1);
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(command)).Status);
        Assert.Equal(2, harness.Store.CurrentSnapshot!.Operations.Count);

        var terminalHarness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        Assert.Equal(
            HumanInputResponseLifecycleMutationStatus.Committed,
            (await terminalHarness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
                request,
                terminalHarness.Store.CurrentSnapshot!.Request.Head,
                "clock-terminal-first",
                "clock-terminal-first-response"))).Status);
        var cancel = HumanInputRequestLifecycleTransitionTestSupport.Command(
            terminalHarness.LifecycleHarness,
            HumanInputRequestLifecycleOperationKind.Cancel,
            "clock-terminal-cancel",
            request.RequestId);
        Assert.Equal(
            HumanInputRequestLifecycleMutationStatus.Committed,
            (await terminalHarness.LifecycleHarness.Service.MutateAsync(cancel)).Status);
        var cancelled = terminalHarness.LifecycleHarness.Store.Snapshot(request.RequestId)!;
        terminalHarness.Store.ReplaceLifecycle(cancelled);
        terminalHarness.Authenticator.ActorId = HumanInputResponseLifecycleTestData.Actor("user-two");
        terminalHarness.Time.UtcNow = firstTime.AddTicks(-1);

        var terminal = await terminalHarness.Service.MutateAsync(HumanInputResponseLifecycleTestData.Submit(
            request,
            cancelled.Head,
            "clock-terminal-rollback",
            "clock-terminal-rollback-response"));

        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Conflict, terminal.Status);
        Assert.Null(terminal.Operation);
        Assert.Single(terminalHarness.Store.Commits);
        Assert.Single(terminalHarness.Store.CurrentSnapshot!.Operations);
    }

    private static async Task<(HumanInputResponseLifecycleHarness Harness, HumanInputResponseReference Reference)> SeedManualResponseAsync(
        HumanInputRequest request,
        string suffix)
    {
        var harness = await HumanInputResponseLifecycleHarness.CreateAsync(request);
        var submit = HumanInputResponseLifecycleTestData.Submit(
            request,
            harness.Store.CurrentSnapshot!.Request.Head,
            $"submit-{suffix}",
            $"response-{suffix}");
        Assert.Equal(HumanInputResponseLifecycleMutationStatus.Committed, (await harness.Service.MutateAsync(submit)).Status);
        return (harness, Reference(request, Assert.Single(harness.Store.CurrentSnapshot!.Responses)));
    }

    private static HumanInputResponseReference Reference(HumanInputRequest request, HumanInputResponseArtifact response)
    {
        Assert.True(HumanInputResponseReference.TryCreate(request, response, out var reference, out var validation));
        Assert.True(validation.IsValid);
        return reference!;
    }
}
