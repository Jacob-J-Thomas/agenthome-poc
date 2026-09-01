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

public sealed class HumanReviewContinuationPublicationProcessLossTests
{
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
}
