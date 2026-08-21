namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Reports one fail-closed operational posture read.</summary>
public sealed record GovernedLoopOperationalPostureResult(
    GovernedLoopOperationalPostureReadStatus Status,
    GovernedLoopOperationalPostureSnapshot? Snapshot,
    string ReasonCode);
