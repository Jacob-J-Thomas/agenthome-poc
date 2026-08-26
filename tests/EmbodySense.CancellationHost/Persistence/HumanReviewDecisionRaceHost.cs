using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewDecisionRaceHost
{
    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string identity, string decisionKindText, string readyPath, string releasePath, string resultPath)
    {
        if (!Enum.TryParse<HumanReviewDecisionKind>(decisionKindText, true, out var decisionKind) || !Enum.IsDefined(decisionKind))
        {
            return 2;
        }

        var paths = new WorkspacePaths(workspaceRoot);
        using var source = new CustomLoopRunStore(paths);
        var current = await source.GetAsync(runId);
        if (current?.HumanReview is null)
        {
            return 3;
        }

        using var inner = new CustomLoopRunStore(paths);
        var service = new HumanReviewDecisionService(
            new HumanReviewAdmissionRaceGateStore(inner, readyPath, releasePath),
            new HumanReviewDecisionHostAuthorizer(),
            new HumanReviewDecisionHostClock(current.UpdatedAtUtc.AddMinutes(1)));
        var result = await service.DecideAsync(new HumanReviewDecisionCommand(current.Id, current.LifecycleVersion, "race-" + identity, decisionKind, decisionKind == HumanReviewDecisionKind.RequestInformation ? "A redacted information request." : null));
        await File.WriteAllTextAsync(resultPath, result.Status + "|" + result.Receipt?.Disposition);
        return result.Status is HumanReviewDecisionServiceStatus.Accepted or HumanReviewDecisionServiceStatus.Conflict ? 0 : 4;
    }
}
