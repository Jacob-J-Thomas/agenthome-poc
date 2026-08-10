using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

internal static class HumanInputLifecycleTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static HumanInputRequest Request(
        string requestId = "request-one",
        string requestVersionId = "version-one",
        HumanInputPrivacyClass privacy = HumanInputPrivacyClass.Private,
        string purpose = "Collect one bounded datum.",
        string prompt = "Provide the requested value.",
        HumanInputResponseSchema? schema = null,
        HumanInputEligibleRespondent[]? respondents = null,
        HumanInputRequestBinding? binding = null,
        DateTimeOffset? requestedAtUtc = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        var request = new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            requestId,
            requestVersionId,
            binding ?? new HumanInputRequestBinding("workspace-one", "governed-loop", "loop-revision-one", "node-one", "run-one", "checkpoint-one"),
            purpose,
            prompt,
            schema ?? new HumanInputResponseSchema(HumanInputResponseKind.Text, 128, null, null, null),
            privacy,
            respondents ?? [new HumanInputEligibleRespondent("user-one", "route-one")],
            new HumanInputTiming(requestedAtUtc ?? Now, expiresAtUtc ?? Now.AddHours(1)),
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstEligibleResponse),
            new HumanInputContinuationBinding(HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly, "node-one", "checkpoint-one"),
            string.Empty);
        return HumanInputRequestHash.Apply(request);
    }

    internal static HumanInputRequest StructuredRequest()
    {
        var choices = new[]
        {
            new HumanInputChoice("choice-one", "First choice"),
            new HumanInputChoice("choice-two", "Second choice")
        };
        return Request(
            schema: new HumanInputResponseSchema(
                HumanInputResponseKind.Structured,
                null,
                null,
                [
                    new HumanInputStructuredFieldSchema("field-one", HumanInputStructuredFieldKind.Text, true, 128, null),
                    new HumanInputStructuredFieldSchema("field-two", HumanInputStructuredFieldKind.Choice, false, null, choices)
                ],
                null),
            respondents:
            [
                new HumanInputEligibleRespondent("user-one", "route-one"),
                new HumanInputEligibleRespondent("user-two", "route-two")
            ]);
    }

    internal static HumanInputRequestReference Reference(HumanInputRequest request)
    {
        Assert.True(HumanInputRequestReference.TryCreate(request, out var reference, out var validation), string.Join(',', validation.Errors.Select(error => error.Code)));
        return reference!;
    }

    internal static HumanInputRequestLifecycleHead Head(
        HumanInputRequest request,
        long lifecycleVersion = 1,
        HumanInputRequestLifecycleStatus status = HumanInputRequestLifecycleStatus.Pending,
        int reminderCount = 0,
        string? supersedesRequestId = null,
        string? supersededByRequestId = null,
        string operationId = "operation-one",
        DateTimeOffset? updatedAtUtc = null)
        => new(
            1,
            request.RequestId,
            lifecycleVersion,
            status,
            Reference(request),
            reminderCount,
            supersedesRequestId,
            supersededByRequestId,
            operationId,
            updatedAtUtc ?? request.Timing.RequestedAtUtc);

    internal static HumanInputRequestLifecycleOperationEvidence Evidence(
        HumanInputRequestLifecycleOperationKind kind,
        HumanInputRequestLifecycleHead? previousHead,
        HumanInputRequestLifecycleHead? resultHead,
        HumanInputRequest? candidateRequest = null,
        HumanInputRequestLifecycleOperationOutcome outcome = HumanInputRequestLifecycleOperationOutcome.Committed,
        HumanInputRequestLifecycleOperationFailureCode failureCode = HumanInputRequestLifecycleOperationFailureCode.None,
        string targetRequestId = "request-one",
        string? relatedRequestId = null,
        HumanInputRequestLifecycleHead? relatedPreviousHead = null,
        HumanInputRequestLifecycleHead? relatedResultHead = null,
        string operationId = "operation-two",
        DateTimeOffset? recordedAtUtc = null,
        HumanInputRequest? expectedArtifact = null)
    {
        var requiresGrant = kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;
        var isCreate = kind == HumanInputRequestLifecycleOperationKind.Create;
        expectedArtifact ??= Request(requestId: targetRequestId);
        return new HumanInputRequestLifecycleOperationEvidence(
            1,
            operationId,
            Hash('a'),
            kind,
            outcome,
            failureCode,
            targetRequestId,
            isCreate ? 0 : previousHead?.LifecycleVersion ?? 1,
            isCreate ? HumanInputRequestLifecycleStatus.Unknown : HumanInputRequestLifecycleStatus.Pending,
            isCreate ? null : previousHead?.CurrentRequest ?? Reference(expectedArtifact),
            isCreate ? null : expectedArtifact.Binding,
            previousHead,
            resultHead,
            relatedRequestId,
            relatedPreviousHead,
            relatedResultHead,
            candidateRequest is null ? null : Reference(candidateRequest),
            Actor(),
            Reason(),
            requiresGrant ? Grant() : null,
            Hash('b'),
            requiresGrant ? Hash('c') : null,
            recordedAtUtc ?? Now.AddMinutes(1));
    }

    internal static HumanInputRequest Rerouted(HumanInputRequest previous, string version = "version-two")
        => Rehash(previous with
        {
            RequestVersionId = version,
            EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "route-two")]
        });

    internal static HumanInputRequest Amended(HumanInputRequest previous, string version = "version-two")
        => Rehash(previous with { RequestVersionId = version, Prompt = "Provide the amended bounded value." });

    internal static HumanInputRequest Rehash(HumanInputRequest request) => HumanInputRequestHash.Apply(request with { RequestHash = string.Empty });

    internal static string Hash(char value) => new(value, 64);

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("user-owner", out var actor, out _));
        return actor!;
    }

    private static AuthorityPurpose Reason()
    {
        Assert.True(AuthorityPurpose.TryParse("Manage one exact bounded Human Input request.", out var reason, out _));
        return reason!;
    }

    private static AuthorityGrantReference Grant()
    {
        Assert.True(AuthorityGrantId.TryParse("grant-one", out var grantId, out _));
        Assert.True(AuthorityGrantRevision.TryParse("1", out var revision, out _));
        return new AuthorityGrantReference(grantId!, revision!, "sha256:" + Hash('d'));
    }
}
