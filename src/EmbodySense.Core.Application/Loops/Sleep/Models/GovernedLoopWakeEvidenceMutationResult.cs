using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one optimistic wake-evidence mutation.</summary>
/// <param name="Status">The closed store outcome.</param>
/// <param name="Evidence">The current authenticated wake evidence when available.</param>
public sealed record GovernedLoopWakeEvidenceMutationResult(
    GovernedLoopWakeEvidenceMutationStatus Status,
    GovernedLoopWakeEvidence? Evidence = null);
