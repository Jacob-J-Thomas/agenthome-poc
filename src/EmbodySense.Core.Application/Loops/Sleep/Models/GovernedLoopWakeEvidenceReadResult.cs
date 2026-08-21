using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one deterministic wake-evidence read.</summary>
/// <param name="Status">The closed read status.</param>
/// <param name="Evidence">The wake evidence, present exactly when found.</param>
public sealed record GovernedLoopWakeEvidenceReadResult(
    GovernedLoopSleepStoreReadStatus Status,
    GovernedLoopWakeEvidence? Evidence = null);
