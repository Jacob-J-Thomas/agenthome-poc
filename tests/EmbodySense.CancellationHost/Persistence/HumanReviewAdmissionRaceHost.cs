using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewAdmissionRaceHost
{
    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string identity, string readyPath, string releasePath, string resultPath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var source = new CustomLoopRunStore(paths);
        var current = await source.GetAsync(runId);
        if (current?.Frontier is null || current.SequentialAdapterBinding is null)
        {
            return 2;
        }

        var transition = GovernedLoopSequentialFrontierMachine.ReviewBlockCurrent(current.Frontier, current.SequentialAdapterBinding, null, null, current.UpdatedAtUtc.AddMinutes(1));
        if (transition.Status != GovernedLoopSequentialFrontierTransitionStatus.Applied || transition.Frontier is not GovernedLoopFrontierPosture blocked)
        {
            return 3;
        }

        using var inner = new CustomLoopRunStore(paths);
        var result = await new HumanReviewAdmissionService(new HumanReviewAdmissionRaceGateStore(inner, readyPath, releasePath)).AdmitAsync(new HumanReviewAdmissionCommand(current.Id, current.LifecycleVersion, HumanReviewAdmissionProcessLossHost.CreateRequest(current, blocked, identity), blocked));
        await File.WriteAllTextAsync(resultPath, result.Status.ToString());
        return result.Status is CustomLoopRunStoreStatus.Updated or CustomLoopRunStoreStatus.Conflict ? 0 : 4;
    }
}
