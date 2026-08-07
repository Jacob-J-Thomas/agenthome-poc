namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects the durable outcome of one explicit bounded receipt cleanup request.
/// </summary>
/// <param name="Status">The protocol cleanup status.</param>
/// <param name="Health">The corresponding safe interface health.</param>
/// <param name="IsCommitted">Whether raw evidence was compacted or an equivalent committed outcome was replayed.</param>
/// <param name="ExhaustionReason">The capacity reason, or <c>None</c>.</param>
/// <param name="CleanupBlockReason">The cleanup block reason, or <c>None</c>.</param>
/// <param name="CompactedArtifactCount">The raw artifact count compacted by the operation.</param>
/// <param name="CompactedArtifactUtf8Bytes">The raw artifact bytes compacted by the operation.</param>
/// <param name="Detail">A bounded actionable result detail.</param>
public sealed record LoopReceiptCleanupResponse(
    string Status,
    LoopReceiptRetentionHealth Health,
    bool IsCommitted,
    string ExhaustionReason,
    string CleanupBlockReason,
    int CompactedArtifactCount,
    long CompactedArtifactUtf8Bytes,
    string Detail);
