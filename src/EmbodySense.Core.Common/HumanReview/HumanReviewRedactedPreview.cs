namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewRedactedPreview
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewRedactedPreview {{ Kind = {Kind}, Label = {Label}, DetailHash = {DetailHash}, Detail = [REDACTED] }}";
    }
}
