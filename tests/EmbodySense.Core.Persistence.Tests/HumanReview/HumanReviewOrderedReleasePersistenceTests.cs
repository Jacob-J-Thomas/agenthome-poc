using System.Diagnostics;
using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Tests.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewOrderedReleasePersistenceTests
{
    [Fact]
    public async Task External_process_response_loss_after_release_CAS_restarts_and_replays_one_observable_effect()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var markerPath = workspace.File("approved-effect.marker");
        var claimed = await CreateClaimedPreDispatchApprovalAsync(workspace, paths, markerPath, "approved-response-loss");

        await AssertExpectedHostLossAsync("human-review-ordered-effect-response-loss", "lost the actuator response", workspace.RootPath, claimed.Id, markerPath);
        Assert.Equal(claimed.HumanReview?.Request.Binding.EffectAttempt?.OperationId + Environment.NewLine, await File.ReadAllTextAsync(markerPath));
        Assert.Equal(GovernedLoopEffectPhase.DispatchBoundaryReached, (await ReadEffectAttemptAsync(paths, claimed)).Payload.Phase);

        var resultPath = workspace.File("approved-response-loss-restart");
        await AssertEffectHostCompletedAsync(workspace.RootPath, claimed.Id, markerPath, resultPath);
        using var restarted = new CustomLoopRunStore(paths);
        var durable = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        await AssertSingleApprovedEffectAsync(paths, markerPath, durable);
        var durableVersion = durable.LifecycleVersion;
        var markerWrite = File.GetLastWriteTimeUtc(markerPath);

        var replayPath = workspace.File("approved-response-loss-replay");
        await AssertEffectHostCompletedAsync(workspace.RootPath, claimed.Id, markerPath, replayPath);
        var replayed = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        Assert.Equal(durableVersion, replayed.LifecycleVersion);
        Assert.Equal(markerWrite, File.GetLastWriteTimeUtc(markerPath));
        await AssertSingleApprovedEffectAsync(paths, markerPath, replayed);
    }

    [Fact]
    public async Task External_process_loss_after_release_compare_exchange_restarts_before_one_effect_dispatch()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var markerPath = workspace.File("release-cas.marker");
        var claimed = await CreateClaimedPreDispatchApprovalAsync(workspace, paths, markerPath, "approved-release-cas-loss");

        await AssertExpectedHostLossAsync(
            "human-review-ordered-effect-process-loss",
            "test host process crashed",
            workspace.RootPath,
            claimed.Id,
            markerPath,
            CustomLoopRunPublicationBoundary.TargetProven.ToString());
        Assert.False(File.Exists(markerPath));
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, (await ReadEffectAttemptAsync(paths, claimed)).Payload.Phase);

        var resultPath = workspace.File("release-cas-restart");
        await AssertEffectHostCompletedAsync(workspace.RootPath, claimed.Id, markerPath, resultPath);
        using var restarted = new CustomLoopRunStore(paths);
        await AssertSingleApprovedEffectAsync(paths, markerPath, Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id)));
    }

    [Fact]
    public async Task Concurrent_external_approved_releasers_converge_on_one_release_result_and_observable_effect()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var markerPath = workspace.File("concurrent-release.marker");
        var claimed = await CreateClaimedPreDispatchApprovalAsync(workspace, paths, markerPath, "approved-release-race");
        var releasePath = workspace.File("approved-release-race-go");
        var firstReadyPath = workspace.File("approved-release-race-first-ready");
        var secondReadyPath = workspace.File("approved-release-race-second-ready");
        var firstResultPath = workspace.File("approved-release-race-first-result");
        var secondResultPath = workspace.File("approved-release-race-second-result");
        using var first = CancellationHostProcess.Start("human-review-ordered-effect-race", workspace.RootPath, claimed.Id, markerPath, firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.Start("human-review-ordered-effect-race", workspace.RootPath, claimed.Id, markerPath, secondReadyPath, releasePath, secondResultPath);
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

        var firstStatus = await ReadOptionalAsync(firstResultPath);
        var secondStatus = await ReadOptionalAsync(secondResultPath);
        Assert.Contains(first.ExitCode, new[] { 0, 3 });
        Assert.Contains(second.ExitCode, new[] { 0, 3 });
        Assert.Contains(firstStatus, new[] { HumanReviewContinuationReleaseStatus.Completed.ToString(), HumanReviewContinuationReleaseStatus.Unavailable.ToString() });
        Assert.Contains(secondStatus, new[] { HumanReviewContinuationReleaseStatus.Completed.ToString(), HumanReviewContinuationReleaseStatus.Unavailable.ToString() });
        Assert.Contains(HumanReviewContinuationReleaseStatus.Completed.ToString(), new[] { firstStatus, secondStatus });
        Assert.DoesNotContain("NeedsReview", new[] { await firstError, await secondError });
        _ = await firstOutput;
        _ = await secondOutput;

        var replayResultPath = workspace.File("approved-release-race-replay-result");
        await AssertEffectHostCompletedAsync(workspace.RootPath, claimed.Id, markerPath, replayResultPath);
        using var restarted = new CustomLoopRunStore(paths);
        await AssertSingleApprovedEffectAsync(paths, markerPath, Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id)));
    }

    [Fact]
    public async Task External_replay_while_the_winner_owns_the_effect_attempt_is_nonmutating_and_later_converges()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var markerPath = workspace.File("owned-effect.marker");
        var claimed = await CreateClaimedPreDispatchApprovalAsync(workspace, paths, markerPath, "owned-effect-replay");
        var ownerReadyPath = workspace.File("owned-effect-ready");
        var ownerReleasePath = workspace.File("owned-effect-release");
        var ownerResultPath = workspace.File("owned-effect-owner-result");
        using var owner = CancellationHostProcess.Start(
            "human-review-ordered-effect-owner-barrier",
            workspace.RootPath,
            claimed.Id,
            markerPath,
            ownerReadyPath,
            ownerReleasePath,
            ownerResultPath);
        var ownerOutput = owner.StandardOutput.ReadToEndAsync();
        var ownerError = owner.StandardError.ReadToEndAsync();
        try
        {
            await WaitForFileAsync(ownerReadyPath, TimeSpan.FromSeconds(30));
            using var reader = new CustomLoopRunStore(paths);
            var beforeLoser = Assert.IsType<CustomLoopRunRecord>(await reader.GetAsync(claimed.Id));
            var beforeRun = CustomLoopRunArtifactSerializer.Serialize(beforeLoser);
            var beforeAudit = await ReadOptionalAsync(paths.EventsLogPath);
            var ownedAttempt = await ReadEffectAttemptAsync(paths, beforeLoser);
            Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, ownedAttempt.Payload.Phase);
            Assert.NotNull(ownedAttempt.DispatchAuthorityEvidenceHash);
            Assert.False(File.Exists(markerPath));

            var loserResultPath = workspace.File("owned-effect-loser-result");
            using (var loser = CancellationHostProcess.Start("human-review-ordered-effect", workspace.RootPath, claimed.Id, markerPath, loserResultPath))
            {
                var loserOutput = loser.StandardOutput.ReadToEndAsync();
                var loserError = loser.StandardError.ReadToEndAsync();
                await loser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(loser.ExitCode == 3, $"Expected an unavailable ownership-conflict replay; exit={loser.ExitCode}; stdout={await loserOutput}; stderr={await loserError}");
            }
            Assert.Equal(HumanReviewContinuationReleaseStatus.Unavailable.ToString(), await File.ReadAllTextAsync(loserResultPath));

            var afterLoser = Assert.IsType<CustomLoopRunRecord>(await reader.GetAsync(claimed.Id));
            Assert.Equal(beforeRun, CustomLoopRunArtifactSerializer.Serialize(afterLoser));
            Assert.Equal(beforeAudit, await ReadOptionalAsync(paths.EventsLogPath));
            Assert.Null(afterLoser.FailureCode);
            Assert.DoesNotContain(afterLoser.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptFailed);
            Assert.DoesNotContain(afterLoser.Events, item => item.SequentialNodeEvidence?.Kind == CustomLoopSequentialNodeEvidenceKind.AmbiguityAttention);

            await File.WriteAllTextAsync(ownerReleasePath, "release");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!File.Exists(ownerReleasePath)) await File.WriteAllTextAsync(ownerReleasePath, "release");
            await StopAsync(owner);
        }

        Assert.True(owner.ExitCode == 0, $"Effect owner failed; exit={owner.ExitCode}; result={await ReadOptionalAsync(ownerResultPath)}; stdout={await ownerOutput}; stderr={await ownerError}");
        Assert.Equal(HumanReviewContinuationReleaseStatus.Completed.ToString(), await File.ReadAllTextAsync(ownerResultPath));
        var replayResultPath = workspace.File("owned-effect-final-replay-result");
        await AssertEffectHostCompletedAsync(workspace.RootPath, claimed.Id, markerPath, replayResultPath);
        using var restarted = new CustomLoopRunStore(paths);
        await AssertSingleApprovedEffectAsync(paths, markerPath, Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id)));
    }

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

    private static async Task<CustomLoopRunRecord> CreateClaimedPreDispatchApprovalAsync(TestWorkspace workspace, WorkspacePaths paths, string markerPath, string identity)
    {
        var context = CustomLoopSequentialEvidenceStoreTests.CreatePreDispatchEffectContext(identity, CapabilityWorkspaceScopeId.Create(paths.RootPath));
        using var store = new CustomLoopRunStore(paths);
        Assert.Equal(CustomLoopRunStoreStatus.Created, (await store.CreateAsync(context.Run)).Status);
        var auditCompleted = new CustomLoopRunEvent(
            2,
            "admission-audit-" + identity,
            context.Run.UpdatedAtUtc.AddTicks(1),
            CustomLoopRunEventKind.AdmissionAuditCompleted,
            null,
            null,
            null,
            "Admission audit completed.",
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);
        var audited = context.Run with
        {
            LifecycleVersion = 2,
            UpdatedAtUtc = auditCompleted.TimestampUtc,
            Events = [.. context.Run.Events, auditCompleted],
        };
        Assert.Equal(CustomLoopRunStoreStatus.Updated, (await store.UpdateAsync(audited, context.Run.LifecycleVersion)).Status);
        var parkPath = workspace.File(identity + "-park-result");
        await AssertHostCompletedAsync(
            CustomLoopOrderedRunStatus.Paused.ToString(),
            "human-review-ordered-effect-park",
            parkPath,
            workspace.RootPath,
            context.Run.Id,
            markerPath,
            parkPath);
        var parked = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(context.Run.Id));
        Assert.Equal(HumanReviewPurpose.PreDispatchEffect, parked.HumanReview?.Request.Purpose);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, parked.Frontier?.Payload.Status);
        Assert.False(File.Exists(markerPath));

        var decidedAtUtc = parked.UpdatedAtUtc.AddTicks(1);
        var decision = await new HumanReviewDecisionService(
            store,
            new PreDispatchHumanReviewDecisionAuthorizer(),
            new HumanReviewDecisionStoreTestClock(decidedAtUtc)).DecideAsync(
                new HumanReviewDecisionCommand(parked.Id, parked.LifecycleVersion, "approve-" + identity, HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        var publication = await new HumanReviewContinuationPublicationService(store, new HumanReviewContinuationRunStore(store)).PublishAsync(parked.Id);
        Assert.Contains(publication.Status, new[] { HumanReviewContinuationStoreMutationStatus.Committed, HumanReviewContinuationStoreMutationStatus.Replayed });
        var published = Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(parked.Id));
        var review = Assert.IsType<HumanReviewRunState>(published.HumanReview);
        var wake = Assert.IsType<HumanReviewContinuationWake>(review.Continuation?.Wake);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var claimedAtUtc = published.UpdatedAtUtc.AddTicks(1);
        var claim = HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            HumanReviewContinuationClaim.CurrentSchemaVersion,
            "claim-" + identity,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "process-effect-worker",
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "process-effect-test", "claim-" + identity, claimedAtUtc, string.Empty)),
            string.Empty));
        var claimed = await new HumanReviewContinuationRunStore(store).ClaimAsync(published.Id, published.LifecycleVersion, claim);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, claimed.Status);
        var durableClaim = Assert.IsType<CustomLoopRunRecord>(claimed.Run);
        var effect = Assert.IsType<HumanReviewEffectAttemptBinding>(durableClaim.HumanReview?.Request.Binding.EffectAttempt);
        var retainedAttempt = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(durableClaim.HumanReview!.Request.Binding.WorkspaceId, effect.OperationId, effect.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, retainedAttempt.Status);
        Assert.NotNull(retainedAttempt.Attempt);
        return durableClaim;
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

    private static Task AssertEffectHostCompletedAsync(string workspaceRoot, string runId, string markerPath, string resultPath)
        => AssertHostCompletedAsync(
            HumanReviewContinuationReleaseStatus.Completed.ToString(),
            "human-review-ordered-effect",
            resultPath,
            workspaceRoot,
            runId,
            markerPath,
            resultPath);

    private static async Task AssertHostCompletedAsync(string expectedResult, string command, string resultPath, params string[] arguments)
    {
        using var process = CancellationHostProcess.Start([command, .. arguments]);
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

        var retainedResult = File.Exists(resultPath) ? await File.ReadAllTextAsync(resultPath) : "<missing>";
        Assert.True(process.ExitCode == 0, $"Host `{command}` failed; exit={process.ExitCode}; result={retainedResult}; stdout={await output}; stderr={await error}");
        Assert.Equal(expectedResult, await File.ReadAllTextAsync(resultPath));
    }

    private static async Task AssertExpectedHostLossAsync(string command, string expectedError, params string[] arguments)
    {
        using var process = CancellationHostProcess.Start([command, .. arguments]);
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
        Assert.True(process.ExitCode != 0 && errorText.Contains(expectedError, StringComparison.OrdinalIgnoreCase), $"Expected host loss from `{command}`; exit={process.ExitCode}; stdout={outputText}; stderr={errorText}");
    }

    private static async Task<GovernedLoopEffectAttempt> ReadEffectAttemptAsync(WorkspacePaths paths, CustomLoopRunRecord run)
    {
        var binding = Assert.IsType<HumanReviewEffectAttemptBinding>(run.HumanReview?.Request.Binding.EffectAttempt);
        var read = await ((IGovernedLoopEffectAttemptReadStore)new GovernedLoopEffectAttemptStore(paths)).ReadAsync(run.HumanReview!.Request.Binding.WorkspaceId, binding.OperationId, binding.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, read.Status);
        return Assert.IsType<GovernedLoopEffectAttempt>(read.Attempt);
    }

    private static async Task AssertSingleApprovedEffectAsync(WorkspacePaths paths, string markerPath, CustomLoopRunRecord durable)
    {
        var validation = CustomLoopRunValidator.Validate(durable);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        Assert.True(durable.Status == CustomLoopRunStatus.Completed, $"Expected Completed, actual={durable.Status}; failure={durable.FailureCode}:{durable.FailureDetail}; last={durable.Events.LastOrDefault()?.Kind}:{durable.Events.LastOrDefault()?.Detail}");
        Assert.Equal(GovernedLoopFrontierStatus.Completed, durable.Frontier?.Payload.Status);
        var continuation = Assert.IsType<HumanReviewContinuationState>(durable.HumanReview?.Continuation);
        var completion = Assert.IsType<HumanReviewContinuationCompletion>(continuation.Completion);
        Assert.Equal(HumanReviewContinuationReleaseKind.PreDispatchEffect, completion.ReleaseReceipt.Kind);
        Assert.Equal(HumanReviewContinuationReleaseDisposition.Released, completion.ReleaseReceipt.Disposition);
        Assert.Single(durable.Events, item => string.Equals(item.EventId, completion.ReleaseReceipt.ReleaseOperationId, StringComparison.Ordinal));
        Assert.Single(durable.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted && string.Equals(item.StepId, "workspace-action", StringComparison.Ordinal));
        var effect = await ReadEffectAttemptAsync(paths, durable);
        Assert.Equal(GovernedLoopEffectPhase.Committed, effect.Payload.Phase);
        Assert.Equal(effect.Payload.OperationId + Environment.NewLine, await File.ReadAllTextAsync(markerPath));
        Assert.Single(Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*.head"));
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

    private static async Task<string> ReadOptionalAsync(string path)
        => File.Exists(path) ? await File.ReadAllTextAsync(path) : "missing";

    private static async Task StopAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }
}
