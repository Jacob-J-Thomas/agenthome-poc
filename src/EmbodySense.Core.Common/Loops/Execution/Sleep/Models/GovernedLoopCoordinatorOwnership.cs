namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one exact local background-coordinator ownership claim.</summary>
/// <param name="SchemaVersion">The ownership schema version, which must be 1.</param>
/// <param name="CoordinatorId">The stable local coordinator identity.</param>
/// <param name="OwnerId">The unique local process-instance owner identity.</param>
/// <param name="OwnershipEpoch">The positive monotonic ownership epoch.</param>
/// <param name="AcquiredAtUtc">The trusted UTC instant at which ownership was acquired.</param>
/// <param name="ContentHash">The canonical hash over the complete ownership evidence except this field.</param>
public sealed record GovernedLoopCoordinatorOwnership(
    int SchemaVersion,
    string CoordinatorId,
    string OwnerId,
    long OwnershipEpoch,
    DateTimeOffset AcquiredAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental ownership schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;
}
