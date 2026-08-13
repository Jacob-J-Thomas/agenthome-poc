namespace EmbodySense.Core.Application.Loops.Sleep.Models;

/// <summary>Reports one authoritative current-posture read.</summary>
/// <param name="Status">The closed read status.</param>
/// <param name="Posture">The current posture, present exactly when found.</param>
public sealed record GovernedLoopSleepCurrentPostureReadResult(
    GovernedLoopSleepCurrentPostureReadStatus Status,
    GovernedLoopSleepCurrentPosture? Posture = null);
