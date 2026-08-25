namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Identifies one exact approval continuation reservation.</summary>
/// <param name="ReservationId">The globally unique reservation identity.</param>
/// <param name="ReservationHash">The exact canonical reservation hash.</param>
public sealed record HumanReviewContinuationReservationReference(string ReservationId, string ReservationHash);
