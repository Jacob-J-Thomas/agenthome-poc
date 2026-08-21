namespace EmbodySense.Core.Common.Loops.Posture.Models;

/// <summary>Binds one caller operation to exact current authority and optimistic target evidence.</summary>
public sealed record GovernedLoopOperationalControlRequest(
    int SchemaVersion,
    string WorkspaceId,
    string OperationId,
    GovernedLoopOperationalControlKind Kind,
    string TargetId,
    long ExpectedRevision,
    string ExpectedEvidenceHash,
    string ExpectedAuthorityEvidenceHash,
    string ActorId,
    string SurfaceId,
    int MaximumBatchItems = 1)
{
    /// <summary>Gets the only supported request schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopOperationalPostureLimits.CurrentSchemaVersion;
}
