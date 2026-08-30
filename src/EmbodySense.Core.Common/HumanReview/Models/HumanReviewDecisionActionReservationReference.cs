namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one immutable reservation for a non-approval Human Review decision action.</summary>
/// <param name="ReservationId">The reservation identity.</param>
/// <param name="ReservationHash">The canonical hash of the reservation.</param>
public sealed record HumanReviewDecisionActionReservationReference(string ReservationId, string ReservationHash);
