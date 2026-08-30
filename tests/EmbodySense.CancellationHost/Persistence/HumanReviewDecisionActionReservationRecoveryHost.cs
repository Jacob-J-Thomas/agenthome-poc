using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.HumanReview;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.CancellationHost.Persistence;

/// <summary>Runs one bounded restart reconciliation pass for a process that died after retaining a non-approval reservation.</summary>
internal static class HumanReviewDecisionActionReservationRecoveryHost
{
    internal static async Task<int> RunAsync(string workspaceRoot, string runId)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        using var store = new CustomLoopRunStore(paths);
        var reserved = await store.GetAsync(runId);
        var action = reserved?.HumanReview?.DecisionActions.SingleOrDefault(item => item is not null && item.Wake is null && item.Completion is null && item.Retirement is null);
        if (reserved is null || action is null) return 2;

        var observedAtUtc = reserved.UpdatedAtUtc.AddTicks(1);
        var recovery = new HumanReviewDecisionActionRecoveryCoordinator(
            new HumanReviewDecisionActionRunStore(store),
            new HumanReviewDecisionActionReservationRecoveryUnavailableConsumer(),
            new HumanReviewDecisionActionReservationRecoveryUnavailableReleasePort(),
            new HumanReviewDecisionHostClock(observedAtUtc));
        var result = await recovery.RecoverAsync(new(1, null, "action-reservation-restart", TimeSpan.FromMinutes(5)));
        var durable = await store.GetAsync(runId);
        var recovered = durable?.HumanReview?.DecisionActions.SingleOrDefault(item => item is not null && item.Reservation.ReservationHash == action.Reservation.ReservationHash);
        return result.Status == HumanReviewDecisionActionRecoveryStatus.Current
            && result.PublicationItems.SingleOrDefault()?.Status is HumanReviewDecisionActionPublicationRecoveryItemStatus.Published or HumanReviewDecisionActionPublicationRecoveryItemStatus.Replayed
            && recovered?.Wake is not null
            ? 0
            : 3;
    }
}
