namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one immutable optimistic state of an exact wake delivery.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="EvidenceVersion">The positive contiguous optimistic evidence version.</param>
/// <param name="Identity">The deterministic wake identity.</param>
/// <param name="Disposition">The closed durable disposition.</param>
/// <param name="ContinuationOperationId">The stable continuation operation identity, required after preparation.</param>
/// <param name="ContinuationEvidenceHash">The exact committed continuation evidence hash, present only after conclusive commit.</param>
/// <param name="DispositionEvidenceReference">The bounded evidence reference explaining a non-commit terminal disposition.</param>
/// <param name="RecordedAtUtc">The trusted UTC instant at which this evidence was recorded.</param>
/// <param name="ContentHash">The canonical hash over the complete immutable evidence except this field.</param>
public sealed record GovernedLoopWakeEvidence(
    int SchemaVersion,
    long EvidenceVersion,
    GovernedLoopWakeIdentity Identity,
    GovernedLoopWakeDisposition Disposition,
    string? ContinuationOperationId,
    string? ContinuationEvidenceHash,
    string? DispositionEvidenceReference,
    DateTimeOffset RecordedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental wake-evidence schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the deterministic wake identity.</summary>
    public GovernedLoopWakeIdentity Identity { get; } = GovernedLoopSleepContractCopy.Copy(Identity);
}
