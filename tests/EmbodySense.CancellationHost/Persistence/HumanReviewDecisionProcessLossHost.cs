using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewDecisionProcessLossHost
{
    private const int ProcessLossExitCode = 176;

    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary)
            || !Enum.IsDefined(boundary)
            || boundary == 0)
        {
            return 2;
        }

        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary));
        var current = await store.GetAsync(runId);
        if (current?.HumanReview is null)
        {
            return 3;
        }

        var result = await new HumanReviewDecisionService(
            store,
            new HumanReviewDecisionHostAuthorizer(),
            new HumanReviewDecisionHostClock(current.UpdatedAtUtc.AddMinutes(1)))
            .DecideAsync(new HumanReviewDecisionCommand(current.Id, current.LifecycleVersion, "process-loss-approve", HumanReviewDecisionKind.Approve, null));
        return result.Status == HumanReviewDecisionServiceStatus.Accepted ? 0 : 4;
    }

    private static ValueTask ExitAfterBoundaryAsync(CustomLoopRunPublicationBoundary currentBoundary, CustomLoopRunPublicationBoundary requestedBoundary)
    {
        if (currentBoundary == requestedBoundary)
        {
            Console.Error.WriteLine($"The test host process crashed after `{currentBoundary}`.");
            Console.Error.Flush();
            Environment.Exit(ProcessLossExitCode);
        }

        return ValueTask.CompletedTask;
    }
}
