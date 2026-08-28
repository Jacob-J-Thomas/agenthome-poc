using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.Tests.Governance.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal static class HumanInputRequestLifecycleTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 10, 18, 30, 0, TimeSpan.Zero);

    internal static HumanInputRequest Request(
        string requestId = "request-one",
        string requestVersionId = "request-version-one",
        string workspaceId = "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string loopGraphId = "governed-loop",
        string loopRevisionId = "revision-1",
        string prompt = "Private prompt value",
        string routingReference = "private-route-one",
        DateTimeOffset? requestedAtUtc = null,
        DateTimeOffset? expiresAtUtc = null)
    {
        var request = new HumanInputRequest(
            HumanInputRequest.CurrentSchemaVersion,
            requestId,
            requestVersionId,
            new HumanInputRequestBinding(
                workspaceId,
                loopGraphId,
                loopRevisionId,
                "node-one",
                "run-one",
                "checkpoint-one"),
            "Collect bounded input for this run.",
            prompt,
            new HumanInputResponseSchema(HumanInputResponseKind.Text, 240, null, null, null),
            HumanInputPrivacyClass.Private,
            [new HumanInputEligibleRespondent("user-one", "respondent-one", routingReference)],
            new HumanInputTiming(requestedAtUtc ?? Now, expiresAtUtc ?? Now.AddHours(1)),
            new HumanInputResponsePolicy(HumanInputResponsePolicyKind.FirstValid, null, null),
            new HumanInputContinuationBinding(
                HumanInputContinuationPolicyKind.BoundNodeAndCheckpointOnly,
                "node-one",
                "checkpoint-one"),
            string.Empty);
        return HumanInputRequestHash.Apply(request);
    }

    internal static HumanInputRequestBinding Binding(
        string workspaceId = "workspace-sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        string loopGraphId = "governed-loop",
        string loopRevisionId = "revision-1")
        => new(
            workspaceId,
            loopGraphId,
            loopRevisionId,
            "node-one",
            "run-one",
            "checkpoint-one");

    internal static HumanInputRequestReference Reference(HumanInputRequest request)
    {
        Assert.True(HumanInputRequestReference.TryCreate(request, out var reference, out var validation));
        Assert.True(validation.IsValid);
        return reference!;
    }

    internal static AuthorityGrant Grant(
        AuthorityGrantLifecycleStatus status = AuthorityGrantLifecycleStatus.Active,
        AuthorityGrantBoundary? boundary = null)
        => AuthorityGrantApplicationTestFixture.Grant(
            status: status,
            boundary: boundary,
            recordedAtUtc: Now.AddMinutes(-10));

    internal static AuthorityGrantReference GrantReference(AuthorityGrant grant)
        => new(grant.GrantId, grant.Revision, grant.ContentHash);

    internal static AuthorityGrantResolution ActiveResolution(
        AuthorityGrant grant,
        DateTimeOffset? evaluatedAtUtc = null)
        => new(
            AuthorityGrantResolutionStatus.Active,
            GrantReference(grant),
            grant,
            grant.RequestedCeiling,
            Hash('d'),
            evaluatedAtUtc ?? Now);

    internal static HumanInputRequestLifecycleCommand Command(
        HumanInputRequestLifecycleOperationKind kind,
        string operationId,
        string requestId,
        AuthorityGrantReference? grantReference,
        HumanInputRequest? candidate = null,
        HumanInputRequestLifecycleHead? expected = null,
        HumanInputRequestBinding? expectedBinding = null)
    {
        var command = new HumanInputRequestLifecycleCommand(
            HumanInputRequestLifecycleCommand.CurrentSchemaVersion,
            operationId,
            kind,
            requestId,
            expected?.LifecycleVersion ?? 0,
            expected?.Status ?? HumanInputRequestLifecycleStatus.Unknown,
            expected?.CurrentRequest,
            expected is null ? null : expectedBinding ?? Binding(),
            candidate,
            grantReference,
            AuthorityGrantApplicationTestFixture.Purpose("Operate one bounded Human Input lifecycle."),
            string.Empty);
        return HumanInputRequestLifecycleCommandHash.Apply(command);
    }

    internal static string Hash(char value) => new(value, 64);
}
