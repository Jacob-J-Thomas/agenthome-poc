namespace EmbodySense.Core.Common.Governance.Tools.Models;

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
