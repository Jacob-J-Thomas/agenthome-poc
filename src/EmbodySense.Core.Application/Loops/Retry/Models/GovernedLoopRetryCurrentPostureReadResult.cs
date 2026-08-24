namespace EmbodySense.Core.Application.Loops.Retry.Models;

/// <summary>Reports an exact current retry-posture read or a conservative failure.</summary>
/// <param name="Status">The closed read status.</param>
/// <param name="Posture">The exact posture only when found.</param>
public sealed record GovernedLoopRetryCurrentPostureReadResult(
    GovernedLoopRetryCurrentPostureReadStatus Status,
    GovernedLoopRetryCurrentPosture? Posture = null);
