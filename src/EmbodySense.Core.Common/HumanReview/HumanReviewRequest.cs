namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewRequest
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewRequest {{ SchemaVersion = {SchemaVersion}, RequestId = {RequestId}, RequestOperationId = {RequestOperationId}, Purpose = {Purpose}, RequestedDecisionCount = {(RequestedDecisions.IsDefault ? 0 : RequestedDecisions.Length)}, ApprovalScope = {ApprovalScope?.Kind}, Timing = {Timing}, RequestHash = {RequestHash}, EligibleReviewers = [REDACTED], Previews = [REDACTED], Provenance = [REDACTED] }}";
    }
}
