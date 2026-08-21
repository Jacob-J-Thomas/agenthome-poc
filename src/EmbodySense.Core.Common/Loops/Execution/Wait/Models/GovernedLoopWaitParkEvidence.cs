using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait.Models;

/// <summary>Records the exact Wait condition and sleeping checkpoint produced after the frontier was durably parked.</summary>
/// <param name="SchemaVersion">The park-evidence schema version, which must be 1.</param>
/// <param name="Condition">The exact typed Wait condition admitted for the activation.</param>
/// <param name="Checkpoint">The exact durable sleep checkpoint published for the parked frontier.</param>
/// <param name="ParkedAtUtc">The trusted UTC instant at which the Waiting frontier became durable.</param>
/// <param name="ContentHash">The canonical hash over the complete park evidence except this field.</param>
public sealed record GovernedLoopWaitParkEvidence(
    int SchemaVersion,
    GovernedLoopWaitCondition Condition,
    GovernedLoopSleepCheckpoint Checkpoint,
    DateTimeOffset ParkedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental park-evidence schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopWaitContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the admitted Wait condition.</summary>
    public GovernedLoopWaitCondition Condition { get; } = GovernedLoopWaitContractCopy.Copy(Condition);

    /// <summary>Gets a defensive copy of the exact published sleeping checkpoint.</summary>
    public GovernedLoopSleepCheckpoint Checkpoint { get; } = GovernedLoopWaitContractCopy.Copy(Checkpoint);
}
