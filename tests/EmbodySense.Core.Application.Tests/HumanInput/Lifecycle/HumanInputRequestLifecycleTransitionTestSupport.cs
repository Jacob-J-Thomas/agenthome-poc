using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal static class HumanInputRequestLifecycleTransitionTestSupport
{
    internal static async Task<HumanInputRequestLifecycleMutationResult> SeedAsync(
        HumanInputRequestLifecycleHarness harness,
        HumanInputRequest request,
        string operationId = "seed-create-request")
    {
        var command = HumanInputRequestLifecycleTestData.Command(
            HumanInputRequestLifecycleOperationKind.Create,
            operationId,
            request.RequestId,
            HumanInputRequestLifecycleTestData.GrantReference(harness.Grant),
            request);
        var result = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Committed, result.Status);
        return result;
    }

    internal static HumanInputRequestLifecycleCommand Command(
        HumanInputRequestLifecycleHarness harness,
        HumanInputRequestLifecycleOperationKind kind,
        string operationId,
        string requestId,
        HumanInputRequest? candidate = null,
        HumanInputRequestLifecycleHead? expected = null,
        HumanInputRequestBinding? expectedBinding = null)
    {
        var snapshot = harness.Store.Snapshot(requestId);
        expected ??= snapshot?.Head;
        expectedBinding ??= snapshot?.RequestVersions
            .SingleOrDefault(request => expected?.CurrentRequest.Matches(request) == true)
            ?.Binding;
        return HumanInputRequestLifecycleTestData.Command(
            kind,
            operationId,
            requestId,
            RequiresGrant(kind) ? HumanInputRequestLifecycleTestData.GrantReference(harness.Grant) : null,
            candidate,
            expected,
            expectedBinding);
    }

    internal static HumanInputRequest RerouteCandidate(HumanInputRequest previous, string versionId = "request-version-rerouted")
        => HumanInputRequestHash.Apply(previous with
        {
            RequestVersionId = versionId,
            EligibleRespondents = [new HumanInputEligibleRespondent("user-two", "private-route-two")],
            RequestHash = string.Empty,
        });

    internal static HumanInputRequest AmendCandidate(
        HumanInputRequest previous,
        string versionId = "request-version-amended",
        string prompt = "Private amended prompt value")
        => HumanInputRequestHash.Apply(previous with
        {
            RequestVersionId = versionId,
            Prompt = prompt,
            RequestHash = string.Empty,
        });

    internal static HumanInputRequest SupersedeCandidate(
        HumanInputRequest previous,
        string requestId = "request-two",
        string versionId = "request-two-version-one")
        => HumanInputRequestHash.Apply(previous with
        {
            RequestId = requestId,
            RequestVersionId = versionId,
            Timing = previous.Timing with { RequestedAtUtc = HumanInputRequestLifecycleTestData.Now },
            RequestHash = string.Empty,
        });

    internal static void ResetCalls(HumanInputRequestLifecycleHarness harness)
    {
        harness.Resolver.Calls.Clear();
        harness.Authorizer.Requests.Clear();
        harness.Store.MutationReads.Clear();
        harness.Store.Commits.Clear();
    }

    internal static async Task AssertDurableReplayAsync(
        HumanInputRequestLifecycleHarness harness,
        HumanInputRequestLifecycleCommand command,
        HumanInputRequestLifecycleMutationStatus firstStatus,
        HumanInputRequestLifecycleOperationFailureCode failureCode)
    {
        var first = await harness.Service.MutateAsync(command);
        Assert.Equal(firstStatus, first.Status);
        Assert.Equal(failureCode, first.Proof?.FailureCode);
        Assert.Single(harness.Store.Commits);

        var replay = await harness.Service.MutateAsync(command);
        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Replayed, replay.Status);
        Assert.Equal(failureCode, replay.Proof?.FailureCode);
        Assert.Single(harness.Store.Commits);
    }

    private static bool RequiresGrant(HumanInputRequestLifecycleOperationKind kind)
        => kind is HumanInputRequestLifecycleOperationKind.Create
            or HumanInputRequestLifecycleOperationKind.Remind
            or HumanInputRequestLifecycleOperationKind.Reroute
            or HumanInputRequestLifecycleOperationKind.Amend
            or HumanInputRequestLifecycleOperationKind.Supersede;
}
