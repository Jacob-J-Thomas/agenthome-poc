using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait.Models;

/// <summary>Records the exact prepared wake and Waiting-to-Running frontier continuation for one parked Wait.</summary>
/// <param name="SchemaVersion">The continuation-evidence schema version, which must be 1.</param>
/// <param name="ParkEvidenceHash">The exact hash of the corresponding immutable park evidence.</param>
/// <param name="PreparedWakeEvidence">The exact prepared wake evidence admitted before the frontier continuation.</param>
/// <param name="PreResumeFrontierVersion">The exact current Waiting frontier version observed immediately before continuation.</param>
/// <param name="PreResumeFrontierHash">The exact current Waiting frontier hash observed immediately before continuation.</param>
/// <param name="ResumedFrontierVersion">The contiguous optimistic frontier version committed by continuation.</param>
/// <param name="ResumedFrontierHash">The exact Running frontier hash committed by continuation.</param>
/// <param name="ResumedAtUtc">The trusted UTC instant at which the exact activation resumed.</param>
/// <param name="ContentHash">The canonical hash over the complete continuation evidence except this field.</param>
public sealed record GovernedLoopWaitContinuationEvidence(
    int SchemaVersion,
    string ParkEvidenceHash,
    GovernedLoopWakeEvidence PreparedWakeEvidence,
    long PreResumeFrontierVersion,
    string PreResumeFrontierHash,
    long ResumedFrontierVersion,
    string ResumedFrontierHash,
    DateTimeOffset ResumedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental continuation-evidence schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopWaitContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the exact prepared wake evidence.</summary>
    public GovernedLoopWakeEvidence PreparedWakeEvidence { get; } = GovernedLoopWaitContractCopy.Copy(PreparedWakeEvidence);
}
