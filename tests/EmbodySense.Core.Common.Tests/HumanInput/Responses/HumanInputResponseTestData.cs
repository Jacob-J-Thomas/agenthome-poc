using System.Collections.Immutable;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Responses;

internal static class HumanInputResponseTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 15, 0, 0, TimeSpan.Zero);

    internal static HumanInputRequest Request(HumanInputResponsePolicyKind policyKind = HumanInputResponsePolicyKind.FirstValid, int? requiredResponseCount = null, ImmutableArray<string>? orderedRoleIds = null, HumanInputEligibleRespondent[]? respondents = null)
    {
        var request = new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            "request-one",
            "request-version-one",
            new HumanInputRequestBinding("workspace-one", "loop-one", "revision-one", "node-one", "run-one", "checkpoint-one"),
            "Collect one bounded response.",
            "Provide untrusted response data only.",
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 128, null, null, null),
            HumanInputPrivacyClass.Private,
            respondents ??
            [
                new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "role-two", "route-two"),
                new HumanInputEligibleRespondent("user-three", "role-three", "route-three"),
                new HumanInputEligibleRespondent("selector-one", "role-selector", "route-selector")
            ],
            new HumanInputTiming(Now, Now.AddHours(1)),
            new HumanInputResponsePolicy(policyKind, requiredResponseCount, orderedRoleIds),
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, "node-one", "checkpoint-one"),
            string.Empty);
        return HumanInputRequestHash.Apply(request);
    }

    internal static HumanInputResponseArtifact Artifact(HumanInputRequest request, string responseId = "response-one", string actorId = "user-one", string roleId = "role-one", string text = "same-value", DateTimeOffset? submittedAtUtc = null, string? explanation = null)
    {
        Assert.True(AuthorityActorId.TryParse(actorId, out var actor, out _));
        Assert.True(HumanInputRequestReference.TryCreate(request, out var requestReference, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        var artifact = new HumanInputResponseArtifact(
            HumanInputResponseArtifact.CurrentSchemaVersion,
            responseId,
            requestReference!,
            request.Binding,
            actor!,
            roleId,
            submittedAtUtc ?? Now.AddMinutes(1),
            request.PrivacyClass,
            new HumanInputResponseValue(HumanInputResponseKind.Text, text, null, null, null, null),
            explanation,
            string.Empty,
            string.Empty);
        return HumanInputResponseArtifactHash.Apply(artifact);
    }

    internal static HumanInputResponseReference Reference(HumanInputRequest request, HumanInputResponseArtifact artifact)
    {
        Assert.True(HumanInputResponseReference.TryCreate(request, artifact, out var reference, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return reference!;
    }

    internal static HumanInputResponseSelection Selection(HumanInputRequest request, IReadOnlyList<HumanInputResponseArtifact> selected, string selectionId = "selection-one", string? selectorActorId = null, string? selectorRoleId = null, DateTimeOffset? selectedAtUtc = null)
    {
        AuthorityActorId? actor = null;
        if (selectorActorId is not null)
        {
            Assert.True(AuthorityActorId.TryParse(selectorActorId, out actor, out _));
        }
        Assert.True(HumanInputRequestReference.TryCreate(request, out var requestReference, out _));
        var selection = new HumanInputResponseSelection(
            HumanInputResponseSelection.CurrentSchemaVersion,
            selectionId,
            requestReference!,
            request.ResponsePolicy.Kind,
            selected.Select(response => Reference(request, response)).ToImmutableArray(),
            actor,
            selectorRoleId,
            selectedAtUtc ?? Now.AddMinutes(10),
            string.Empty);
        return HumanInputResponseSelectionHash.Apply(selection);
    }

    internal static HumanInputRequestLifecycleHead PendingHead(HumanInputRequest request)
        => new(1, request.RequestId, 1, HumanInputRequestLifecycleStatus.Pending, RequestReference(request), 0, null, null, "create-one", Now);

    internal static HumanInputRequestLifecycleHead AnsweredHead(HumanInputRequest request, HumanInputResponseSelection selection, string operationId = "operation-one")
        => PendingHead(request) with
        {
            LifecycleVersion = 2,
            Status = HumanInputRequestLifecycleStatus.Answered,
            LastOperationId = operationId,
            UpdatedAtUtc = selection.SelectedAtUtc,
            AnswerSelection = HumanInputResponseSelectionReference.Create(selection)
        };

    internal static HumanInputResponseOperationEvidence Evidence(
        HumanInputRequest request,
        HumanInputResponseOperationKind kind,
        HumanInputResponseOperationOutcome outcome = HumanInputResponseOperationOutcome.Committed,
        HumanInputResponseOperationFailureCode failureCode = HumanInputResponseOperationFailureCode.None,
        HumanInputResponseArtifact? submitted = null,
        IReadOnlyList<HumanInputResponseArtifact>? targets = null,
        HumanInputResponseSelection? selection = null,
        HumanInputRequestLifecycleHead? previousHead = null,
        HumanInputRequestLifecycleHead? resultHead = null,
        string operationId = "operation-one",
        string actorId = "user-one",
        string? actorRoleId = "role-one",
        long? expectedLifecycleVersion = null,
        HumanInputRequestBinding? expectedBinding = null,
        HumanInputRequestBinding? observedBinding = null)
    {
        Assert.True(AuthorityActorId.TryParse(actorId, out var actor, out _));
        var previous = previousHead ?? PendingHead(request);
        return new HumanInputResponseOperationEvidence(
            HumanInputResponseOperationEvidence.CurrentSchemaVersion,
            operationId,
            Hash('a'),
            kind,
            outcome,
            failureCode,
            RequestReference(request),
            expectedBinding ?? request.Binding,
            failureCode == HumanInputResponseOperationFailureCode.RequestNotFound ? null : observedBinding ?? request.Binding,
            expectedLifecycleVersion ?? previous.LifecycleVersion,
            HumanInputRequestLifecycleStatus.Pending,
            failureCode == HumanInputResponseOperationFailureCode.RequestNotFound ? null : previous,
            failureCode == HumanInputResponseOperationFailureCode.RequestNotFound ? null : resultHead ?? previous,
            submitted is null ? null : Reference(request, submitted),
            (targets ?? []).Select(target => Reference(request, target)).ToImmutableArray(),
            selection is null ? null : HumanInputResponseSelectionReference.Create(selection),
            actor!,
            actorRoleId,
            Hash('b'),
            Hash('c'),
            selection?.SelectedAtUtc ?? Now.AddMinutes(5));
    }

    internal static HumanInputRequestReference RequestReference(HumanInputRequest request)
    {
        Assert.True(HumanInputRequestReference.TryCreate(request, out var reference, out _));
        return reference!;
    }

    internal static string Hash(char value) => new(value, HumanInputLimits.Sha256HexCharacters);
}
