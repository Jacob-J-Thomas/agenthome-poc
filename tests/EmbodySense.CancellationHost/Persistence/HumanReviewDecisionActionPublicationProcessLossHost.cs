using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewDecisionActionPublicationProcessLossHost
{
    private const int ProcessLossExitCode = 178;

    internal static async Task<int> RunAsync(string workspaceRoot, string runId, string boundaryText)
    {
        if (!Enum.TryParse<CustomLoopRunPublicationBoundary>(boundaryText, out var boundary) || !Enum.IsDefined(boundary) || boundary == 0) return 2;

        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths, null, (currentBoundary, _) => ExitAfterBoundaryAsync(currentBoundary, boundary));
        var current = await store.GetAsync(runId);
        var action = current?.HumanReview?.DecisionActions.SingleOrDefault(item => item is not null && item.Wake is null && item.Completion is null && item.Retirement is null);
        if (current is null || action is null || action.Reservation.Decision.Kind is not (HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation)) return 3;

        var reservation = new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash);
        var result = await new HumanReviewDecisionActionPublicationService(store, new HumanReviewDecisionActionRunStore(store)).PublishAsync(new(current.Id, reservation));
        return result.Status == HumanReviewDecisionActionStoreMutationStatus.Committed ? 0 : 4;
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
