namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Identifies one deterministic wake delivery for one immutable checkpoint.</summary>
/// <param name="SchemaVersion">The wake-identity schema version, which must be 1.</param>
/// <param name="WakeId">The deterministic lowercase SHA-256 identity derived from all authenticated wake coordinates.</param>
/// <param name="CheckpointId">The deterministic checkpoint identity.</param>
/// <param name="CheckpointHash">The exact checkpoint content hash.</param>
/// <param name="WakeMode">The checkpoint's exact wake mode.</param>
/// <param name="AuthenticatedEventReference">The exact authenticated event reference for an event wake.</param>
/// <param name="AuthenticationEvidenceHash">The exact authentication-evidence hash for an event wake.</param>
/// <param name="ContentHash">The canonical hash over the complete wake identity except this field.</param>
public sealed record GovernedLoopWakeIdentity(
    int SchemaVersion,
    string WakeId,
    string CheckpointId,
    string CheckpointHash,
    GovernedLoopWakeMode WakeMode,
    string? AuthenticatedEventReference,
    string? AuthenticationEvidenceHash,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental wake-identity schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;
}
