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
using EmbodySense.Tests.Support;
using static EmbodySense.Core.Persistence.Tests.HumanReview.HumanReviewContinuationRunStoreTests;

namespace EmbodySense.Core.Persistence.Tests.HumanReview;

public sealed class HumanReviewContinuationRetirementProcessLossTests
{
    [Theory]
    [InlineData(CustomLoopRunPublicationBoundary.StagedFileFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.CanonicalRenamed)]
    [InlineData(CustomLoopRunPublicationBoundary.ParentDirectoryFlushed)]
    [InlineData(CustomLoopRunPublicationBoundary.TargetProven)]
    public async Task Separate_process_loss_at_each_retirement_boundary_preserves_one_replayable_predecessor_or_successor_and_excludes_completion(CustomLoopRunPublicationBoundary boundary)
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var approved = await CreateApprovedRunAsync(paths, "continuation-retirement-loss");
        var review = Assert.IsType<HumanReviewRunState>(approved.HumanReview);
        var reservation = Assert.IsType<HumanReviewContinuationReservation>(review.ContinuationReservation);
        var wake = Wake(review, reservation, approved.UpdatedAtUtc.AddSeconds(1), "wake-retirement-process-loss");
        var initial = HumanReviewContinuationContractHash.ApplyState(new HumanReviewContinuationState(1, wake, ImmutableArray<HumanReviewContinuationClaim>.Empty, null, null, string.Empty));
        var claim = Claim(wake, reservation, wake.PublishedAtUtc.AddMinutes(1), "claim-retirement-process-loss");
        CustomLoopRunRecord claimed;
        using (var store = new CustomLoopRunStore(paths))
        {
            var continuations = new HumanReviewContinuationRunStore(store);
            var published = Assert.IsType<CustomLoopRunRecord>((await continuations.PublishAsync(approved.Id, approved.LifecycleVersion, initial)).Run);
            claimed = Assert.IsType<CustomLoopRunRecord>((await continuations.ClaimAsync(published.Id, published.LifecycleVersion, claim)).Run);
        }

        var expected = Retirement(wake, reservation, claim.ClaimedAtUtc.AddSeconds(1), "retirement-process-loss");
        var predecessorBytes = CustomLoopRunArtifactSerializer.Serialize(claimed);
        await RunTransitionProcessLossAsync(workspace, claimed.Id, "retirement", boundary);

        using var restarted = new CustomLoopRunStore(paths);
        var recovered = Assert.IsType<CustomLoopRunRecord>(await restarted.GetAsync(claimed.Id));
        Assert.True(CustomLoopRunValidator.Validate(recovered).IsValid, string.Join(Environment.NewLine, CustomLoopRunValidator.Validate(recovered).Errors));
        if (recovered.HumanReview?.Continuation?.Retirement is null)
        {
            Assert.Equal(predecessorBytes, CustomLoopRunArtifactSerializer.Serialize(recovered));
        }
        else
        {
            Assert.Equal(expected.RetirementHash, recovered.HumanReview.Continuation.Retirement.RetirementHash);
        }

        var reconciliation = await new HumanReviewContinuationRunStore(restarted).RetireAsync(
            recovered.Id,
            recovered.LifecycleVersion,
            new HumanReviewContinuationClaimReference(claim.ClaimId, claim.ClaimHash),
            expected);
        Assert.Contains(reconciliation.Status, new[] { HumanReviewContinuationMutationStatus.Committed, HumanReviewContinuationMutationStatus.Replayed });
        var completion = Completion(review.Request, wake, reservation, claim, claim.ClaimedAtUtc.AddSeconds(1), "completion-after-retirement-process-loss");
        Assert.Equal(HumanReviewContinuationMutationStatus.Conflict, (await new HumanReviewContinuationRunStore(restarted).CompleteAsync(reconciliation.Run!.Id, reconciliation.Run.LifecycleVersion, completion)).Status);
    }
}
