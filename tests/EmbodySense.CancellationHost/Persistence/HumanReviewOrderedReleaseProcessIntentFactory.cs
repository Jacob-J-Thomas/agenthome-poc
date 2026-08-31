using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.CancellationHost.Persistence;

internal static class HumanReviewOrderedReleaseProcessIntentFactory
{
    internal static bool TryCreate(CustomLoopRunRecord? run, out HumanReviewDecisionActionIntent? intent, out DateTimeOffset releaseAtUtc)
    {
        intent = null;
        releaseAtUtc = default;
        var action = run?.HumanReview?.DecisionActions.SingleOrDefault(item => item is not null
            && item.Reservation.Decision.Kind == HumanReviewDecisionKind.Reject
            && item.Wake is not null
            && !item.Claims.IsDefaultOrEmpty
            && item.Retirement is null);
        var claim = action?.Claims[^1];
        if (run is null || action?.Wake is null || claim is null) return false;

        releaseAtUtc = claim.ClaimedAtUtc.AddTicks(1);
        intent = new HumanReviewDecisionActionIntent(
            run.Id,
            run.LifecycleVersion,
            new HumanReviewRequestReference(run.HumanReview!.Request.RequestId, run.HumanReview.Request.RequestHash),
            action.Reservation.Decision,
            new HumanReviewDecisionActionWakeReference(action.Wake.WakeId, action.Wake.WakeHash),
            new HumanReviewDecisionActionClaimReference(claim.ClaimId, claim.ClaimHash),
            new HumanReviewDecisionActionReservationReference(action.Reservation.ReservationId, action.Reservation.ReservationHash),
            action.ExpectedGeneration,
            ActionOperationId(action.Reservation.ReservationHash));
        return true;
    }

    private static string ActionOperationId(string reservationHash)
        => "action-operation-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reservationHash)))[..24];
}
