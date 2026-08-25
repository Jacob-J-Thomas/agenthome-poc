namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewReviewerScope
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewReviewerScope {{ ReviewerRoleId = {ReviewerRoleId}, ScopeIds = [REDACTED] }}";
    }
}
