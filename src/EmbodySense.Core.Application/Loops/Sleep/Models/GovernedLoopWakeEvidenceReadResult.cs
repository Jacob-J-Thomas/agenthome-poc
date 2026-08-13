using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one deterministic wake-evidence read.</summary>
/// <param name="Status">The closed read status.</param>
/// <param name="Evidence">The wake evidence, present exactly when found.</param>
/// <param name="PreparedEvidence">The exact retained prepared predecessor when the current evidence belongs to a continuation attempt.</param>
public sealed record GovernedLoopWakeEvidenceReadResult(
    GovernedLoopSleepStoreReadStatus Status,
    GovernedLoopWakeEvidence? Evidence = null,
    GovernedLoopWakeEvidence? PreparedEvidence = null);
