namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewEvidence
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewEvidence {{ SchemaVersion = {SchemaVersion}, EvidenceId = {EvidenceId}, Request = {Request}, Kind = {Kind}, Decision = {Decision}, RecordedAtUtc = {RecordedAtUtc:O}, PreviousEvidenceHash = {PreviousEvidenceHash}, EvidenceHash = {EvidenceHash}, Previews = [REDACTED], Provenance = [REDACTED] }}";
    }
}
