namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewDecisionProposal
{
    /// <inheritdoc />
    public override string ToString() => $"HumanReviewDecisionProposal {{ SchemaVersion = {SchemaVersion}, DecisionOperationId = {DecisionOperationId}, Kind = {Kind}, ProposalHash = {ProposalHash}, Detail = [REDACTED] }}";
}
