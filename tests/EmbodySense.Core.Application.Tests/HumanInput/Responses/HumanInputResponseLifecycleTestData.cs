using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

internal static class HumanInputResponseLifecycleTestData
{
    internal static readonly DateTimeOffset Now = HumanInputRequestLifecycleTestData.Now;

    internal static HumanInputRequest Request(
        HumanInputResponsePolicyKind policyKind = HumanInputResponsePolicyKind.FirstValid,
        int? requiredResponseCount = null,
        ImmutableArray<string>? orderedRoleIds = null,
        HumanInputEligibleRespondent[]? respondents = null,
        DateTimeOffset? requestedAtUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        int maxTextCharacters = 80,
        string requestId = "response-request",
        string requestVersionId = "response-request-v1")
    {
        respondents ??=
        [
            new HumanInputEligibleRespondent("user-one", "role-one", "route-one"),
            new HumanInputEligibleRespondent("user-two", "role-two", "route-two"),
            new HumanInputEligibleRespondent("selector-one", "selector-role", "route-selector"),
        ];
        return HumanInputRequestHash.Apply(
            new HumanInputRequest(
                HumanInputRequest.CurrentSchemaVersion,
                requestId,
                requestVersionId,
                Binding(),
                "Collect one bounded response.",
                "Provide the requested response.",
                new HumanInputResponseSchema(HumanInputResponseKind.Text, maxTextCharacters, null, null, null),
                HumanInputPrivacyClass.Private,
                respondents,
                new HumanInputTiming(requestedAtUtc ?? Now, expiresAtUtc ?? Now.AddHours(1)),
                new HumanInputResponsePolicy(policyKind, requiredResponseCount, orderedRoleIds),
                new HumanInputContinuationBinding(
                    HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly,
                    "node-one",
                    "checkpoint-one"),
                string.Empty));
    }

    internal static HumanInputRequestBinding Binding(string workspaceId = "workspace-one")
        => new(workspaceId, "governed-loop", "revision-1", "node-one", "run-one", "checkpoint-one");

    internal static HumanInputRequestReference Reference(HumanInputRequest request)
        => new(1, request.RequestId, request.RequestVersionId, request.RequestHash);

    internal static HumanInputResponseValue Text(string value = "valid response")
        => new(HumanInputResponseKind.Text, value, null, null, null, null);

    internal static HumanInputResponseLifecycleCommand Submit(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        string operationId,
        string responseId,
        HumanInputResponseValue? value = null,
        string? explanation = null,
        HumanInputRequestReference? expectedRequest = null,
        HumanInputRequestBinding? expectedBinding = null,
        long? expectedLifecycleVersion = null)
        => HumanInputResponseLifecycleCommandHash.Apply(
            new HumanInputResponseLifecycleCommand(
                1,
                operationId,
                HumanInputResponseOperationKind.Submit,
                request.RequestId,
                expectedLifecycleVersion ?? head.LifecycleVersion,
                HumanInputRequestLifecycleStatus.Pending,
                expectedRequest ?? head.CurrentRequest,
                expectedBinding ?? request.Binding,
                responseId,
                value ?? Text(),
                explanation,
                [],
                string.Empty));

    internal static HumanInputResponseLifecycleCommand Target(
        HumanInputRequest request,
        HumanInputRequestLifecycleHead head,
        HumanInputResponseOperationKind kind,
        string operationId,
        HumanInputResponseReference target,
        HumanInputRequestReference? expectedRequest = null,
        HumanInputRequestBinding? expectedBinding = null,
        long? expectedLifecycleVersion = null)
        => HumanInputResponseLifecycleCommandHash.Apply(
            new HumanInputResponseLifecycleCommand(
                1,
                operationId,
                kind,
                request.RequestId,
                expectedLifecycleVersion ?? head.LifecycleVersion,
                HumanInputRequestLifecycleStatus.Pending,
                expectedRequest ?? head.CurrentRequest,
                expectedBinding ?? request.Binding,
                null,
                null,
                null,
                [target],
                string.Empty));

    internal static AuthorityActorId Actor(string value)
    {
        Assert.True(AuthorityActorId.TryParse(value, out var actor, out _));
        return actor!;
    }

    internal static string Hash(char value) => new(value, 64);
}
