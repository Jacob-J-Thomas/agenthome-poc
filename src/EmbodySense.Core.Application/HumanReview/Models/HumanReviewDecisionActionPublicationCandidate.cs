using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes one retained wake-less reservation that a bounded recovery pass may publish.</summary>
/// <param name="RunId">The canonical run that retains the reservation.</param>
/// <param name="ExpectedLifecycleVersion">The whole-run version that must still retain the exact reservation.</param>
/// <param name="Request">The immutable request that fixes the action wake expiry.</param>
/// <param name="Action">The validated wake-less action state from which the deterministic wake is derived.</param>
public sealed record HumanReviewDecisionActionPublicationCandidate(string RunId, int ExpectedLifecycleVersion, HumanReviewRequest Request, HumanReviewDecisionActionState Action);
