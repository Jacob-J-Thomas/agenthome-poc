using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleLimitTests
{
    [Fact]
    public async Task Reminder_limit_persists_and_replays_bounded_limit_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, request);
        for (var index = 1; index <= HumanInputRequestLifecycleContractLimits.MaxReminderCount; index++)
        {
            var seed = HumanInputRequestLifecycleTransitionTestSupport.Command(
                harness,
                HumanInputRequestLifecycleOperationKind.Remind,
                $"seed-reminder-{index}",
                request.RequestId);
            Assert.Equal(
                HumanInputRequestLifecycleMutationStatus.Committed,
                (await harness.Service.MutateAsync(seed)).Status);
        }

        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "reminder-limit-receipt",
            request.RequestId);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            HumanInputRequestLifecycleMutationStatus.LimitExceeded,
            HumanInputRequestLifecycleOperationFailureCode.ReminderLimitExceeded);
        Assert.All(harness.Store.MutationReads, read => Assert.Null(read.RelatedRequestId));
    }

    [Fact]
    public async Task Request_version_limit_persists_and_replays_bounded_limit_receipt()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var current = HumanInputRequestLifecycleTestData.Request();
        await HumanInputRequestLifecycleTransitionTestSupport.SeedAsync(harness, current);
        for (var index = 1; index < HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest; index++)
        {
            var candidate = HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
                current,
                $"request-version-{index + 1}",
                $"Private amended prompt value {index}");
            var seed = HumanInputRequestLifecycleTransitionTestSupport.Command(
                harness,
                HumanInputRequestLifecycleOperationKind.Amend,
                $"seed-amendment-{index}",
                current.RequestId,
                candidate);
            Assert.Equal(
                HumanInputRequestLifecycleMutationStatus.Committed,
                (await harness.Service.MutateAsync(seed)).Status);
            current = candidate;
        }

        HumanInputRequestLifecycleTransitionTestSupport.ResetCalls(harness);
        var rejectedCandidate = HumanInputRequestLifecycleTransitionTestSupport.AmendCandidate(
            current,
            "request-version-over-limit",
            "Private prompt beyond retained version limit");
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Amend,
            "request-version-limit-receipt",
            current.RequestId,
            rejectedCandidate);

        await HumanInputRequestLifecycleTransitionTestSupport.AssertDurableReplayAsync(
            harness,
            command,
            HumanInputRequestLifecycleMutationStatus.LimitExceeded,
            HumanInputRequestLifecycleOperationFailureCode.RequestVersionLimitExceeded);
        Assert.All(harness.Store.MutationReads, read => Assert.Null(read.RelatedRequestId));
    }

    [Fact]
    public async Task Lifecycle_versions_outside_schema_one_bound_are_rejected_before_dependencies()
    {
        var harness = new HumanInputRequestLifecycleHarness();
        var request = HumanInputRequestLifecycleTestData.Request();
        var impossibleHead = new HumanInputRequestLifecycleHead(
            HumanInputRequestLifecycleContractLimits.CurrentSchemaVersion,
            request.RequestId,
            HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion + 1,
            HumanInputRequestLifecycleStatus.Pending,
            HumanInputRequestLifecycleTestData.Reference(request),
            0,
            null,
            null,
            "impossible-operation",
            HumanInputRequestLifecycleTestData.Now);
        var command = HumanInputRequestLifecycleTransitionTestSupport.Command(
            harness,
            HumanInputRequestLifecycleOperationKind.Remind,
            "invalid-lifecycle-version",
            request.RequestId,
            expected: impossibleHead);

        var result = await harness.Service.MutateAsync(command);

        Assert.Equal(HumanInputRequestLifecycleMutationStatus.Invalid, result.Status);
        Assert.Contains(
            result.ValidationErrors,
            error => error.Code == HumanInputRequestLifecycleMutationValidationErrorCode.InvalidExpectedState);
        Assert.Empty(harness.Store.MutationReads);
        Assert.Empty(harness.Resolver.Calls);
        Assert.Empty(harness.Authorizer.Requests);
        Assert.Empty(harness.Store.Commits);
    }
}
