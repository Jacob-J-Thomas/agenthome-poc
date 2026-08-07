using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents an explicitly bounded governed receipt cleanup request.
/// </summary>
/// <param name="SchemaVersion">The request schema version.</param>
/// <param name="ArtifactClass">The target receipt artifact class.</param>
/// <param name="OperationId">The cleanup idempotency identity.</param>
/// <param name="Actor">The authenticated cleanup actor.</param>
/// <param name="Surface">The owning runtime surface.</param>
/// <param name="RequestedAtUtc">The request timestamp.</param>
/// <param name="ReplayCutoffUtc">The inclusive exact-replay expiry cutoff.</param>
/// <param name="MaximumArtifactCount">The maximum raw artifacts this request may compact.</param>
/// <param name="MaximumArtifactUtf8Bytes">The maximum raw artifact bytes this request may compact.</param>
public sealed record CustomLoopReceiptCleanupRequest(
    int SchemaVersion,
    CustomLoopReceiptArtifactClass ArtifactClass,
    string OperationId,
    string Actor,
    string Surface,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReplayCutoffUtc,
    int MaximumArtifactCount,
    long MaximumArtifactUtf8Bytes)
{
    /// <summary>
    /// Current governed cleanup request schema version.
    /// </summary>
    public const int CurrentSchemaVersion = 1;
}
