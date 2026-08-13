namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one immutable optimistic lifecycle state for one exact coordinator owner.</summary>
/// <param name="SchemaVersion">The lifecycle schema version, which must be 1.</param>
/// <param name="LifecycleVersion">The positive contiguous optimistic lifecycle version.</param>
/// <param name="Ownership">The exact ownership claim whose lifecycle is described.</param>
/// <param name="Status">The closed lifecycle posture.</param>
/// <param name="UpdatedAtUtc">The trusted UTC instant of this lifecycle state.</param>
/// <param name="TerminalAtUtc">The terminal UTC instant, present exactly for stopped or failed posture.</param>
/// <param name="ContentHash">The canonical hash over the complete lifecycle state except this field.</param>
public sealed record GovernedLoopCoordinatorLifecycle(
    int SchemaVersion,
    long LifecycleVersion,
    GovernedLoopCoordinatorOwnership Ownership,
    GovernedLoopCoordinatorStatus Status,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? TerminalAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental lifecycle schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the exact ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership Ownership { get; } = GovernedLoopSleepContractCopy.Copy(Ownership);
}
