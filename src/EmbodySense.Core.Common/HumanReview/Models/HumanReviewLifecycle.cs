namespace EmbodySense.Core.Common.HumanReview.Models;

/// <summary>Defines one immutable optimistic lifecycle head for an exact Human Review request.</summary>
/// <param name="SchemaVersion">The lifecycle schema version, which must be 1.</param>
/// <param name="Request">The exact immutable request reference.</param>
/// <param name="Status">The current durable lifecycle posture.</param>
/// <param name="LifecycleVersion">The positive optimistic lifecycle version.</param>
/// <param name="UpdatedAtUtc">The trusted UTC timestamp of the durable lifecycle transition.</param>
/// <param name="LastDecision">The exact accepted decision that produced a decision-derived status, if any.</param>
/// <param name="Provenance">The immutable trusted server or coordinator lifecycle provenance.</param>
/// <param name="PreviousLifecycleHash">The optional exact predecessor lifecycle hash.</param>
/// <param name="LifecycleHash">The canonical hash of every behavior-affecting lifecycle field.</param>
public sealed partial record HumanReviewLifecycle(
    int SchemaVersion,
    HumanReviewRequestReference Request,
    HumanReviewLifecycleStatus Status,
    long LifecycleVersion,
    DateTimeOffset UpdatedAtUtc,
    HumanReviewDecisionReference? LastDecision,
    HumanReviewProvenance Provenance,
    string? PreviousLifecycleHash,
    string LifecycleHash)
{
    /// <summary>Gets the only supported lifecycle schema version.</summary>
    public const int CurrentSchemaVersion = HumanReviewContractLimits.CurrentSchemaVersion;

}
