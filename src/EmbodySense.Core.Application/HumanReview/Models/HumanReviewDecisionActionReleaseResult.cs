using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Returns conclusive action completion evidence from a host-owned action adapter.</summary>
public sealed record HumanReviewDecisionActionReleaseResult(HumanReviewDecisionActionReleaseStatus Status, HumanReviewDecisionActionCompletion? Completion = null);
