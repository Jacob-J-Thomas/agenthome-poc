namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one immutable lease heartbeat for one exact coordinator owner.</summary>
/// <param name="SchemaVersion">The heartbeat schema version, which must be 1.</param>
/// <param name="HeartbeatSequence">The positive contiguous heartbeat sequence.</param>
/// <param name="Ownership">The exact ownership claim being renewed.</param>
/// <param name="RecordedAtUtc">The trusted UTC instant at which the heartbeat was recorded.</param>
/// <param name="LeaseExpiresAtUtc">The exclusive trusted UTC lease-expiry boundary.</param>
/// <param name="ContentHash">The canonical hash over the complete heartbeat except this field.</param>
public sealed record GovernedLoopCoordinatorHeartbeat(
    int SchemaVersion,
    long HeartbeatSequence,
    GovernedLoopCoordinatorOwnership Ownership,
    DateTimeOffset RecordedAtUtc,
    DateTimeOffset LeaseExpiresAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental heartbeat schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the exact ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership Ownership { get; } = GovernedLoopSleepContractCopy.Copy(Ownership);
}
