namespace EmbodySense.Core.Common.HumanReview.Models;

public sealed partial record HumanReviewLifecycle
{
    /// <inheritdoc />
    public override string ToString()
    {
        return $"HumanReviewLifecycle {{ SchemaVersion = {SchemaVersion}, Request = {Request}, Status = {Status}, LifecycleVersion = {LifecycleVersion}, UpdatedAtUtc = {UpdatedAtUtc:O}, LastDecision = {LastDecision}, PreviousLifecycleHash = {PreviousLifecycleHash}, LifecycleHash = {LifecycleHash}, Provenance = [REDACTED] }}";
    }
}
