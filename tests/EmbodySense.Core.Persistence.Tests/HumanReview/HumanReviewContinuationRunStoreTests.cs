using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewContinuationRunStoreTests
{
    [Fact]
    public async Task Canonical_store_publishes_claims_takes_over_and_completes_one_approval_across_restart()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-flow");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-flow");
        var publishedState = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using (var store = new CustomLoopRunStore(paths))
        {
            var continuations = new HumanReviewContinuationRunStore(store);
            var publication = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, publishedState);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, publication.Status);
            Assert.Equal(publishedState.StateHash, publication.Run?.HumanReview?.Continuation?.StateHash);

            var published = Assert.IsType<CustomLoopRunRecord>(publication.Run);
            var first = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-flow-one");
            var firstClaim = await continuations.ClaimAsync(published.Id, published.LifecycleVersion, first);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, firstClaim.Status);

            var claimed = Assert.IsType<CustomLoopRunRecord>(firstClaim.Run);
            Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.ClaimAsync(claimed.Id, claimed.LifecycleVersion, first)).Status);
            var divergentFirst = HumanReviewContinuationContractHash.ApplyClaim(first with { WorkerId = "worker-claim-flow-divergent", ClaimHash = string.Empty });
            Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.ClaimAsync(claimed.Id, claimed.LifecycleVersion, divergentFirst)).Status);
            var second = Claim(wake, reservation, first.LeaseExpiresAtUtc.AddTicks(1), "claim-flow-two");
            var takeover = await continuations.ClaimAsync(claimed.Id, claimed.LifecycleVersion, second);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, takeover.Status);

            var takenOver = Assert.IsType<CustomLoopRunRecord>(takeover.Run);
            var staleCompletion = Completion(review.Request, wake, reservation, first, first.ClaimedAtUtc.AddSeconds(1), "completion-stale");
            var stale = await continuations.CompleteAsync(takenOver.Id, takenOver.LifecycleVersion, staleCompletion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, stale.Status);

            var completion = Completion(review.Request, wake, reservation, second, second.ClaimedAtUtc.AddSeconds(1), "completion-flow");
            var completed = await continuations.CompleteAsync(takenOver.Id, takenOver.LifecycleVersion, completion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, completed.Status);
            Assert.Equal(completion.CompletionHash, completed.Run?.HumanReview?.Continuation?.Completion?.CompletionHash);

            var replay = await continuations.CompleteAsync(takenOver.Id, takenOver.LifecycleVersion, completion);
            Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, replay.Status);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        var continuation = Assert.IsType<HumanReviewContinuationState>(persisted.HumanReview?.Continuation);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Equal(2, continuation.Claims.Length);
        Assert.Equal("claim-flow-two", continuation.Claims[^1].ClaimId);
        Assert.NotNull(continuation.Completion);
        Assert.Null(continuation.Retirement);
    }

    [Fact]
    public async Task Publication_and_terminal_retirement_require_exact_replay_and_never_cross_terminal_boundaries()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-retirement");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-retirement");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using var store = new CustomLoopRunStore(paths);
        var continuations = new HumanReviewContinuationRunStore(store);
        var publication = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, publication.Status);
        var replay = await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, replay.Status);
        var divergentWake = HumanReviewContinuationContractHash.ApplyWake(wake with { WakeId = "wake-retirement-divergent", WakeHash = string.Empty });
        var divergent = HumanReviewContinuationContractHash.ApplyState(initial with { Wake = divergentWake, StateHash = string.Empty });
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, divergent)).Status);

        var published = Assert.IsType<CustomLoopRunRecord>(publication.Run);
        var claim = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-retirement");
        var claimed = await continuations.ClaimAsync(published.Id, published.LifecycleVersion, claim);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, claimed.Status);
        var retirement = Retirement(wake, reservation, claim.LeaseExpiresAtUtc.AddTicks(1), "retirement-flow");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.RetireAsync(
            claimed.Run!.Id,
            claimed.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference("claim-retirement-unknown", Hash('d')),
            retirement)).Status);
        var retired = await continuations.RetireAsync(
            claimed.Run!.Id,
            claimed.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            retirement);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, retired.Status);
        Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, (await continuations.RetireAsync(
            claimed.Run.Id,
            claimed.Run.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            retirement)).Status);

        var completion = Completion(review.Request, wake, reservation, claim, claim.ClaimedAtUtc.AddSeconds(1), "completion-after-retirement");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await continuations.CompleteAsync(claimed.Run.Id, claimed.Run.LifecycleVersion, completion)).Status);
    }

    [Fact]
    public async Task Separate_process_claimers_share_one_compare_exchange_winner_and_one_append_only_claim()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-claim-race");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-claim-race");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        using (var store = new CustomLoopRunStore(paths))
        {
            var published = await new HumanReviewContinuationRunStore(store).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.Committed, published.Status);
        }

        var firstReadyPath = Path.Combine(workspace.RootPath, "claim-race-first.ready");
        var secondReadyPath = Path.Combine(workspace.RootPath, "claim-race-second.ready");
        var releasePath = Path.Combine(workspace.RootPath, "claim-race.release");
        var firstResultPath = Path.Combine(workspace.RootPath, "claim-race-first.result");
        var secondResultPath = Path.Combine(workspace.RootPath, "claim-race-second.result");
        using var first = CancellationHostProcess.Start("human-review-continuation-claim-race", workspace.RootPath, approved.Id, "first", firstReadyPath, releasePath, firstResultPath);
        using var second = CancellationHostProcess.Start("human-review-continuation-claim-race", workspace.RootPath, approved.Id, "second", secondReadyPath, releasePath, secondResultPath);
        await WaitForFileAsync(firstReadyPath, TimeSpan.FromSeconds(30));
        await WaitForFileAsync(secondReadyPath, TimeSpan.FromSeconds(30));
        await File.WriteAllTextAsync(releasePath, "release");
        await first.WaitForExitAsync();
        await second.WaitForExitAsync();

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, second.ExitCode);
        var outcomes = new[] { await File.ReadAllTextAsync(firstResultPath), await File.ReadAllTextAsync(secondResultPath) };
        Assert.Equal(1, outcomes.Count(item => item == HumanReviewContinuationMutationStatus.Committed.ToString()));
        Assert.Equal(1, outcomes.Count(item => item == HumanReviewContinuationMutationStatus.Conflict.ToString()));

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        var continuation = Assert.IsType<HumanReviewContinuationState>(persisted.HumanReview?.Continuation);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(persisted).Errors));
        Assert.Single(continuation.Claims);
        Assert.Contains(continuation.Claims[0].ClaimId, new[] { "claim-race-first", "claim-race-second" });
    }

    [Fact]
    public async Task Response_unknown_after_canonical_rename_rereads_the_exact_published_wake_without_republishing()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-response-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-response-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        using (var uncertain = new CustomLoopRunStore(paths, null, (boundary, _) => boundary == CustomLoopRunPublicationBoundary.CanonicalRenamed
            ? ValueTask.FromException(new IOException("response lost after canonical publication"))
            : ValueTask.CompletedTask))
        {
            var result = await new HumanReviewContinuationRunStore(uncertain).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.Replayed, result.Status);
            Assert.Equal(initial.StateHash, result.Run?.HumanReview?.Continuation?.StateHash);
        }

        using var restarted = new CustomLoopRunStore(paths);
        var persisted = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        Assert.Equal(initial.StateHash, persisted.HumanReview?.Continuation?.StateHash);
        Assert.Equal(approved.LifecycleVersion + 1, persisted.LifecycleVersion);
    }

    [Fact]
    public async Task Corrupt_canonical_run_fails_closed_without_rewriting_the_artifact()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-corruption");
        var artifactPath = Path.Combine(paths.CustomLoopRunsPath, approved.LoopId, approved.Id + ".json");
        await File.WriteAllTextAsync(artifactPath, "{");
        var corruptedBytes = await File.ReadAllBytesAsync(artifactPath);
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-corruption");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using var store = new CustomLoopRunStore(paths);
        var result = await new HumanReviewContinuationRunStore(store).PublishAsync(approved.Id, approved.LifecycleVersion, initial);

        Assert.Equal(HumanReviewContinuationMutationStatus.Invalid, result.Status);
        Assert.Equal(corruptedBytes, await File.ReadAllBytesAsync(artifactPath));
    }

    [Fact]
    public async Task Canonical_read_io_failure_and_quota_rejection_are_closed_mutation_results_without_publication()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-unavailable-quota");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-unavailable-quota");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));

        using (var unavailable = new CustomLoopRunStore(paths, null, (_, _, _) => ValueTask.FromException(new IOException("test read unavailable"))))
        {
            var unavailableResult = await new HumanReviewContinuationRunStore(unavailable).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.Unavailable, unavailableResult.Status);
        }

        using (var canonical = new CustomLoopRunStore(paths))
        {
            var quotaResult = await new HumanReviewContinuationRunStore(new QuotaRejectingCustomLoopRunStore(canonical)).PublishAsync(approved.Id, approved.LifecycleVersion, initial);
            Assert.Equal(HumanReviewContinuationMutationStatus.LimitExceeded, quotaResult.Status);
            Assert.Null((await canonical.GetAsync(approved.Id))?.HumanReview?.Continuation);
        }
    }

    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_publication_boundary_converges_on_the_one_exact_wake_or_its_untouched_predecessor(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-publication-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var expectedWake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-process-loss");
        var expected = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, expectedWake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(approved);
        using var process = CancellationHostProcess.Start("human-review-continuation-publication-process-loss", workspace.RootPath, approved.Id, boundary.ToString());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("test host process crashed", await errorTask, StringComparison.OrdinalIgnoreCase);
        _ = await outputTask;
        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(approved.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.StateHash, recovered.HumanReview.Continuation.StateHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).PublishAsync(recovered.Id, recovered.LifecycleVersion, expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        Assert.Equal(expected.StateHash, reconciliation.Run?.HumanReview?.Continuation?.StateHash);
    }

    private static async Task<CustomLoopRunRecord> CreateApprovedRunAsync(WorkspacePaths paths, string identity)
    {
        var admitted = await CustomLoopFrontierStoreTests.PersistHumanReviewAdmissionAsync(paths, identity);
        using var store = new CustomLoopRunStore(paths);
        var decision = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionStoreTestAuthorizer(),
            new HumanReviewDecisionStoreTestClock(admitted.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(admitted.Id, admitted.LifecycleVersion, "approve-" + identity, HumanReviewDecisionKind.Approve, null));
        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, decision.Status);
        return Assert.IsType<CustomLoopRunRecord>(await store.GetAsync(admitted.Id));
    }

    private static HumanReviewContinuationWake Wake(HumanReviewRunState review, HumanReviewContinuationReservation reservation, DateTimeOffset publishedAtUtc, string wakeId)
        => HumanReviewContinuationContractHash.ApplyWake(new HumanReviewContinuationWake(
            1,
            wakeId,
            new HumanReviewRequestReference(review.Request.RequestId, review.Request.RequestHash),
            reservation.Decision,
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            review.Request.Binding.BindingHash,
            1,
            publishedAtUtc,
            review.Request.Timing.ExpiresAtUtc,
            Provenance(wakeId, publishedAtUtc),
            string.Empty));

    private static HumanReviewContinuationClaim Claim(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, DateTimeOffset claimedAtUtc, string claimId)
        => HumanReviewContinuationContractHash.ApplyClaim(new HumanReviewContinuationClaim(
            1,
            claimId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            "worker-" + claimId,
            claimedAtUtc,
            claimedAtUtc.AddMinutes(5),
            Provenance(claimId, claimedAtUtc),
            string.Empty));

    private static HumanReviewContinuationCompletion Completion(HumanReviewRequest request, HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, HumanReviewContinuationClaim claim, DateTimeOffset completedAtUtc, string completionId)
    {
        var receipt = HumanReviewContinuationContractHash.ApplyReleaseReceipt(new HumanReviewContinuationReleaseReceipt(
            1,
            "release-" + completionId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            HumanReviewContinuationReleaseKind.Continuation,
            HumanReviewContinuationReleaseDisposition.Released,
            Hash('a'),
            Hash('b'),
            null,
            string.Empty));
        return HumanReviewContinuationContractHash.ApplyCompletion(new HumanReviewContinuationCompletion(
            1,
            completionId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            receipt,
            completedAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance(completionId, completedAtUtc),
            string.Empty));
    }

    private static HumanReviewContinuationRetirement Retirement(HumanReviewContinuationWake wake, HumanReviewContinuationReservation reservation, DateTimeOffset retiredAtUtc, string retirementId)
        => HumanReviewContinuationContractHash.ApplyRetirement(new HumanReviewContinuationRetirement(
            1,
            retirementId,
            new HumanReviewContinuationWakeReference(wake.WakeId, wake.WakeHash),
            new HumanReviewContinuationReservationReference(reservation.ReservationId, reservation.ReservationHash),
            wake.ExpectedGeneration,
            HumanReviewContinuationOutcome.Blocked,
            retiredAtUtc,
            ImmutableArray<HumanReviewRedactedPreview>.Empty,
            Provenance(retirementId, retiredAtUtc),
            string.Empty));

    private static HumanReviewProvenance Provenance(string correlationId, DateTimeOffset observedAtUtc)
        => HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-continuation-store", correlationId, observedAtUtc, string.Empty));

    private static string Hash(char character) => new(character, HumanReviewContractLimits.Sha256HexCharacters);

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellation.Token);
        }
    }
}
