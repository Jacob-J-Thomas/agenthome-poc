namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Captures one trusted-time read of the complete local-background operational plane.</summary>
public sealed record GovernedLoopOperationalPostureSnapshot(
    int SchemaVersion,
    string WorkspaceId,
    DateTimeOffset ObservedAtUtc,
    string ControlAuthorityEvidenceHash,
    GovernedLoopQueuePosture Queue,
    GovernedLoopScheduleCatalogPosture Schedules,
    GovernedLoopWakeCatalogPosture Wakes,
    GovernedLoopRunCatalogPosture Runs,
    GovernedLoopCoordinatorPosture Coordinator)
{
    /// <summary>Gets the only supported posture schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopOperationalPostureLimits.CurrentSchemaVersion;
}
