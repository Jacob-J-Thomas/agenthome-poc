using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview;

/// <summary>Builds the one deterministic wake that may follow an accepted non-approval reservation.</summary>
/// <remarks>The factory deliberately derives every value from retained canonical state. It accepts no current clock, worker, or caller identity, so a restart cannot create a divergent wake for the same reservation.</remarks>
internal static class HumanReviewDecisionActionWakeFactory
{
    internal static bool TryCreate(HumanReviewRequest request, HumanReviewDecisionActionState retained, out HumanReviewDecisionActionState? expected)
    {
        expected = null;
        try
        {
            if (!HumanReviewDecisionActionContractValidator.ValidateState(request, retained).IsValid
                || retained.Reservation.Decision.Kind is not (HumanReviewDecisionKind.Reject or HumanReviewDecisionKind.Cancel or HumanReviewDecisionKind.RequestInformation))
            {
                return false;
            }

            var wakeId = Id("action-wake", retained.Reservation.ReservationHash);
            var provenance = HumanReviewContractHash.ApplyProvenance(new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "human-review-action-publisher", wakeId, retained.Reservation.ReservedAtUtc, string.Empty));
            var wake = HumanReviewDecisionActionContractHash.ApplyWake(new HumanReviewDecisionActionWake(1, wakeId, retained.Reservation.Request, retained.Reservation.Decision, new(retained.Reservation.ReservationId, retained.Reservation.ReservationHash), retained.BindingHash, retained.ExpectedGeneration, retained.Reservation.ReservedAtUtc, request.Timing.ExpiresAtUtc, provenance, string.Empty));
            var candidate = HumanReviewDecisionActionContractHash.ApplyState(retained with { Wake = wake, StateHash = string.Empty });
            if (retained.Wake is null && !HumanReviewDecisionActionStateTransitionValidator.ValidateTransition(request, retained, candidate).IsValid)
            {
                return false;
            }

            expected = candidate;
            return true;
        }
        catch
        {
            expected = null;
            return false;
        }
    }

    private static string Id(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
