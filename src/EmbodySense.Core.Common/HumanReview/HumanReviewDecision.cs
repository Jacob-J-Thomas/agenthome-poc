namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewDecision
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewDecision {{ SchemaVersion = {SchemaVersion}, DecisionId = {DecisionId}, DecisionOperationId = {DecisionOperationId}, Request = {Request}, Kind = {Kind}, DecidedAtUtc = {DecidedAtUtc:O}, DecisionHash = {DecisionHash}, ReviewerRoleId = [REDACTED], ReviewerScopeIds = [REDACTED], Detail = [REDACTED], Provenance = [REDACTED] }}";
    }
}
