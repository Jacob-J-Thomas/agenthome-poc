namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Carries one interface-requested bounded receipt cleanup without allowing the caller to select audit identity.
/// </summary>
/// <param name="ArtifactClass">The exact supported artifact class name.</param>
/// <param name="OperationId">The caller-provided idempotency identity for this exact cleanup request.</param>
/// <param name="MaximumArtifactCount">The requested raw artifact count limit, bounded by policy.</param>
/// <param name="MaximumArtifactUtf8Bytes">The requested raw artifact byte limit, bounded by policy.</param>
public sealed record LoopReceiptCleanupInput(string ArtifactClass, string OperationId, int MaximumArtifactCount, long MaximumArtifactUtf8Bytes);
