namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Returns a safe decision-operation outcome and detached receipt evidence.</summary>
/// <param name="Status">The normalized operation outcome.</param>
/// <param name="OperationId">The supplied operation identity when safely captured.</param>
/// <param name="Evidence">The detached durable receipt when available.</param>
public sealed record HumanReviewDecisionResult(HumanReviewDecisionStatus Status, string OperationId, HumanReviewDecisionEvidence? Evidence);
