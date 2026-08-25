namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewProvenance
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewProvenance {{ Kind = {Kind}, ObservedAtUtc = {ObservedAtUtc:O}, ProvenanceHash = {ProvenanceHash}, SourceId = [REDACTED], CorrelationId = [REDACTED] }}";
    }
}
