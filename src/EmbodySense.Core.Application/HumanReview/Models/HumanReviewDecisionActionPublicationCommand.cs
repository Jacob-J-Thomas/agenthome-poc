using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Names one exact retained non-approval reservation whose deterministic wake should be published.</summary>
public sealed record HumanReviewDecisionActionPublicationCommand(string RunId, HumanReviewDecisionActionReservationReference Reservation);
