namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines the bounded client decision intent after a trusted boundary has canonicalized it for durable comparison.</summary>
/// <param name="SchemaVersion">The schema version, which must be 1.</param>
/// <param name="DecisionOperationId">The client idempotency identity.</param>
/// <param name="Kind">The requested closed decision kind.</param>
/// <param name="Detail">The optional redacted display-safe detail.</param>
/// <param name="ProposalHash">The server-derived canonical proposal hash.</param>
public sealed partial record HumanReviewDecisionProposal(int SchemaVersion, string DecisionOperationId, HumanReviewDecisionKind Kind, string? Detail, string ProposalHash)
{
    /// <summary>Gets the only supported proposal schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;
}
