namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents a tool result retention reference.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="ManifestPath">The manifest path.</param>
/// <param name="ContentSha256">The content SHA-256.</param>
/// <param name="CharacterCount">The character count.</param>
/// <param name="Utf8ByteCount">The UTF-8 byte count.</param>
/// <param name="ChunkCount">The chunk count.</param>
/// <param name="RetainedAtUtc">The retained at UTC.</param>
/// <param name="EvictedArtifactCount">The evicted artifact count.</param>
/// <param name="Detail">The detail.</param>
public sealed record ToolResultRetentionReference(
    ToolResultRetentionStatus Status,
    string? ManifestPath,
    string? ContentSha256,
    int? CharacterCount,
    long? Utf8ByteCount,
    int? ChunkCount,
    DateTimeOffset? RetainedAtUtc,
    int EvictedArtifactCount,
    string Detail);
