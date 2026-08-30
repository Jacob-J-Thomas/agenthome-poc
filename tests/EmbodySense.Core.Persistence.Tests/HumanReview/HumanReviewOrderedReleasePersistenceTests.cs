using System.Diagnostics;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewOrderedReleasePersistenceTests
{
    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task External_process_loss_during_real_ordered_reject_release_restarts_to_one_durable_terminal_receipt(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var claimed = await CreateClaimedRejectActionAsync(paths, "ordered-release-loss-" + boundary.ToString().ToLowerInvariant());

        await AssertProcessLossAsync(workspace.RootPath, claimed.Id, boundary);
        var resultPath = workspace.File("ordered-release-loss-result-" + boundary);
        await AssertCompletedHostAsync(workspace.RootPath, claimed.Id, resultPath);

        using var restarted = new CustomLoopRunStore(paths);
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        AssertSingleTerminalRejectRelease(durable);
    }

    [Fact]
    public async Task Two_external_ordered_releasers_race_on_one_whole_run_compare_exchange_and_replay_the_same_receipt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var claimed = await CreateClaimedRejectActionAsync(paths, "ordered-release-race");
        var releasePath = workspace.File("ordered-release-race-release");
        var firstReadyPath = workspace.File("ordered-release-race-first-ready");
        var secondReadyPath = workspace.File("ordered-release-race-second-ready");
        var firstResultPath = workspace.File("ordered-release-race-first-result");
        var secondResultPath = workspace.File("ordered-release-race-second-result");
        using var first = CancellationHostProcess.Start("human-review-ordered-release-race", workspace.RootPath, claimed.Id, firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.Start("human-review-ordered-release-race", workspace.RootPath, claimed.Id, secondReadyPath, releasePath, secondResultPath);
        var firstOutput = first.StandardOutput.ReadToEndAsync();
        var firstError = first.StandardError.ReadToEndAsync();
        var secondOutput = second.StandardOutput.ReadToEndAsync();
        var secondError = second.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFileAsync(firstReadyPath, TimeSpan.FromSeconds(30));
            await WaitForFileAsync(secondReadyPath, TimeSpan.FromSeconds(30));
            await File.WriteAllTextAsync(releasePath, "release");
            await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await StopAsync(first);
            await StopAsync(second);
        }

        var firstText = await firstOutput;
        var firstErrorText = await firstError;
        var secondText = await secondOutput;
        var secondErrorText = await secondError;
        Assert.True(first.ExitCode == 0, $"First ordered release failed; stdout={firstText}; stderr={firstErrorText}");
        Assert.True(second.ExitCode == 0, $"Second ordered release failed; stdout={secondText}; stderr={secondErrorText}");
        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Completed.ToString(), await File.ReadAllTextAsync(firstResultPath));
        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Completed.ToString(), await File.ReadAllTextAsync(secondResultPath));

        using var restarted = new CustomLoopRunStore(paths);
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        AssertSingleTerminalRejectRelease(durable);
    }

    private static async Task<CustomLoopRunRecord> CreateClaimedRejectActionAsync(WorkspacePaths paths, string identity)
    {
        var admitted = await CustomLoopFrontierStoreTests.PersistStrictHumanReviewAdmissionAsync(paths, identity);
        using var store = new CustomLoopRunStore(paths);
        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionStoreTestAuthorizer(),
            new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(admitted.Id, admitted.LifecycleVersion, "ordered-release-decision-" + identity, HumanReviewDecisionKind.Reject, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        var reserved = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(reserved.HumanReview).DecisionActions);
        var actions = new HumanReviewDecisionActionRunStore(store);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await new HumanReviewDecisionActionPublicationService(store, actions).PublishAsync(new(reserved.Id, new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash)))).Status);
        var published = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(reserved.Id));
        var publishedAction = Assert.Single(Assert.IsType<HumanReviewRunState>(published.HumanReview).DecisionActions);
        var wake = Assert.IsType<HumanReviewDecisionActionWake>(publishedAction.Wake);
        var claimedAtUtc = wake.PublishedAtUtc.AddMinutes(1);
        var claim = HumanReviewDecisionActionContractHash.ApplyClaim(new HumanReviewDecisionActionClaim(
            HumanReviewDecisionActionClaim.CurrentSchemaVersion,
            "ordered-release-claim-" + identity,
            new HumanReviewDecisionActionWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewDecisionActionReservationReference(publishedAction.Reservation.ReservationId, publishedAction.Reservation.ReservationHash),
            publishedAction.ExpectedGeneration,
            "ordered-release-worker",
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "ordered-release-test", "ordered-release-claim-" + identity, claimedAtUtc, string.Empty)),
            string.Empty));
        var candidate = new HumanReviewDecisionActionRecoveryCandidate(
            published.Id,
            published.LifecycleVersion,
            new HumanReviewRequestReference(published.HumanReview!.Request.RequestId, published.HumanReview.Request.RequestHash),
            publishedAction.Reservation.Decision,
            new HumanReviewDecisionActionWakeReference(wake.WakeId, wake.WakeHash),
            publishedAction.ExpectedGeneration,
            wake.ExpiresAtUtc,
            new HumanReviewDecisionActionReservationReference(publishedAction.Reservation.ReservationId, publishedAction.Reservation.ReservationHash),
            null);
        Assert.Equal(HumanReviewDecisionActionStoreMutationStatus.Committed, (await actions.ClaimAsync(new HumanReviewDecisionActionClaimIntent(candidate, claim))).Status);
        return Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(published.Id));
    }

    private static async Task AssertProcessLossAsync(string workspaceRoot, string runId, CustomLoopRunPublicationBoundary boundary)
    {
        using var process = CancellationHostProcess.Start("human-review-ordered-release-process-loss", workspaceRoot, runId, boundary.ToString());
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await StopAsync(process);
        }

        var outputText = await output;
        var errorText = await error;
        Assert.True(process.ExitCode != 0 && errorText.Contains("test host process crashed", StringComparison.OrdinalIgnoreCase), $"Expected the ordered-release process-loss boundary crash; exit={process.ExitCode}; stdout={outputText}; stderr={errorText}");
    }

    private static async Task AssertCompletedHostAsync(string workspaceRoot, string runId, string resultPath)
    {
        using var process = CancellationHostProcess.Start("human-review-ordered-release", workspaceRoot, runId, resultPath);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            await StopAsync(process);
        }

        var outputText = await output;
        var errorText = await error;
        Assert.True(process.ExitCode == 0, $"Ordered release restart failed; stdout={outputText}; stderr={errorText}");
        Assert.Equal(HumanReviewDecisionActionReleaseStatus.Completed.ToString(), await File.ReadAllTextAsync(resultPath));
    }

    private static void AssertSingleTerminalRejectRelease(CustomLoopRunRecord durable)
    {
        Assert.True(CustomLoopRunValidator.Validate(durable).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(durable).Errors));
        Assert.Equal(CustomLoopRunStatus.Failed, durable.Status);
        Assert.Equal(GovernedLoopFrontierStatus.Failed, durable.Frontier?.Payload.Status);
        Assert.Single(durable.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(durable.HumanReview).DecisionActions);
        Assert.Equal(HumanReviewDecisionActionDisposition.Rejected, action.Completion?.Disposition);
        Assert.Single(action.Claims);
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path)) await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
    }

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
