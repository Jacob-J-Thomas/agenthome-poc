using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents a timestamp-free governed request to perform one bounded receipt cleanup.
/// </summary>
/// <param name="SchemaVersion">The command schema version.</param>
/// <param name="ArtifactClass">The target receipt artifact class.</param>
/// <param name="OperationId">The cleanup idempotency identity.</param>
/// <param name="Actor">The authenticated cleanup actor.</param>
/// <param name="Surface">The owning runtime surface.</param>
/// <param name="MaximumArtifactCount">The maximum raw artifacts this command may compact.</param>
/// <param name="MaximumArtifactUtf8Bytes">The maximum raw artifact bytes this command may compact.</param>
public sealed record CustomLoopReceiptCleanupCommand(
    int SchemaVersion,
    CustomLoopReceiptArtifactClass ArtifactClass,
    string OperationId,
    string Actor,
    string Surface,
    int MaximumArtifactCount,
    long MaximumArtifactUtf8Bytes)
{
    /// <summary>
    /// Current governed cleanup command schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
