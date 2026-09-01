using System.Collections.Immutable;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.HumanReview.Models;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Tests.Verification;
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.HumanReview.HumanReviewContinuationRunStoreTests;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewContinuationClaimProcessLossTests
{
    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_claim_boundary_preserves_one_replayable_predecessor_or_successor_and_rejects_a_stale_worker(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-claim-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-claim-process-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        CustomLoopRunRecord published;
        using (var store = new CustomLoopRunStore(paths))
        {
            published = Assert.IsType<CustomLoopRunRecord>((await new HumanReviewContinuationRunStore(store).PublishAsync(approved.Id, approved.LifecycleVersion, initial)).Run);
        }

        var expected = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(published);
        await RunTransitionProcessLossAsync(workspace, published.Id, "claim", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(published.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation?.Claims.IsEmpty == true)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.ClaimHash, recovered.HumanReview?.Continuation?.Claims.Single().ClaimHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).ClaimAsync(recovered.Id, recovered.LifecycleVersion, expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        var claimed = Assert.IsType<CustomLoopRunRecord>(reconciliation.Run);
        var takeover = Claim(wake, reservation, expected.LeaseExpiresAtUtc.AddTicks(1), "claim-process-loss-takeover");
        var takenOver = await new HumanReviewContinuationRunStore(restarted).ClaimAsync(claimed.Id, claimed.LifecycleVersion, takeover);
        Assert.Equal(HumanReviewContinuationMutationStatus.Committed, takenOver.Status);
        var staleCompletion = Completion(review.Request, wake, reservation, expected, expected.ClaimedAtUtc.AddSeconds(1), "completion-process-loss-stale");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await new HumanReviewContinuationRunStore(restarted).CompleteAsync(takenOver.Run!.Id, takenOver.Run.LifecycleVersion, staleCompletion)).Status);
    }
}
