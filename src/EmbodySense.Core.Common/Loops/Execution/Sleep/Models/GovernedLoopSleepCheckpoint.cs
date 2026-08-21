namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one immutable, durably published sleeping-frontier checkpoint.</summary>
/// <param name="SchemaVersion">The checkpoint schema version, which must be 1.</param>
/// <param name="CheckpointId">The deterministic lowercase SHA-256 identity derived from the immutable binding and wake condition.</param>
/// <param name="Binding">The exact published frontier visit and wait attempt.</param>
/// <param name="WakeMode">The closed wake mode.</param>
/// <param name="WakeDeadlineUtc">The exact timestamp deadline, present only for timestamp wakes.</param>
/// <param name="AuthenticatedEventReference">The bounded event-subscription reference, present only for event wakes.</param>
/// <param name="PublishedAtUtc">The trusted UTC instant at which the checkpoint became durable.</param>
/// <param name="ContentHash">The canonical hash over the complete checkpoint except this field.</param>
public sealed record GovernedLoopSleepCheckpoint(
    int SchemaVersion,
    string CheckpointId,
    GovernedLoopSleepBinding Binding,
    GovernedLoopWakeMode WakeMode,
    DateTimeOffset? WakeDeadlineUtc,
    string? AuthenticatedEventReference,
    DateTimeOffset PublishedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental checkpoint schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the exact sleep binding.</summary>
    public GovernedLoopSleepBinding Binding { get; } = GovernedLoopSleepContractCopy.Copy(Binding);
}
