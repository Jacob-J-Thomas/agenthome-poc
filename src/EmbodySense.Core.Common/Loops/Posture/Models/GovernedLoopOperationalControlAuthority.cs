namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Captures current trusted authority for one operational-control request.</summary>
public sealed record GovernedLoopOperationalControlAuthority(
    int SchemaVersion,
    string WorkspaceId,
    string ActorId,
    string SurfaceId,
    DateTimeOffset ObservedAtUtc,
    string EvidenceHash,
    bool Permitted,
    string ReasonCode)
{
    /// <summary>Gets the only supported authority schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopOperationalPostureLimits.CurrentSchemaVersion;
}
