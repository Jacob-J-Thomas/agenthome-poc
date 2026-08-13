namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one bounded immutable failure for one exact coordinator owner.</summary>
/// <param name="SchemaVersion">The failure schema version, which must be 1.</param>
/// <param name="FailureSequence">The positive contiguous failure sequence for the ownership claim.</param>
/// <param name="Ownership">The exact ownership claim that observed the failure.</param>
/// <param name="Kind">The closed value-free failure category.</param>
/// <param name="DetailEvidenceReference">The optional bounded reference to complete diagnostic evidence.</param>
/// <param name="OccurredAtUtc">The trusted UTC instant at which the failure occurred.</param>
/// <param name="ContentHash">The canonical hash over the complete failure evidence except this field.</param>
public sealed record GovernedLoopCoordinatorFailure(
    int SchemaVersion,
    long FailureSequence,
    GovernedLoopCoordinatorOwnership Ownership,
    GovernedLoopCoordinatorFailureKind Kind,
    string? DetailEvidenceReference,
    DateTimeOffset OccurredAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental failure schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the exact ownership claim.</summary>
    public GovernedLoopCoordinatorOwnership Ownership { get; } = GovernedLoopSleepContractCopy.Copy(Ownership);
}
