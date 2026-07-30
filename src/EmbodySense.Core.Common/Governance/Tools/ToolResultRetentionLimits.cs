namespace EmbodySense.Core.Common.Governance.Tools;

/// <summary>
/// Defines the supported tool result retention limits.
/// </summary>
public static class ToolResultRetentionLimits
{
    /// <summary>
    /// Maximum characters retained from one tool response before chunking.
    /// </summary>
    public const int MaxOutputCharacters = 160_000;

    /// <summary>
    /// Maximum characters stored in one retained-response chunk.
    /// </summary>
    public const int MaxChunkCharacters = 32_000;

    /// <summary>
    /// Maximum retained-response artifacts permitted per workspace.
    /// </summary>
    public const int MaxArtifactsPerWorkspace = 256;

    /// <summary>
    /// Maximum UTF-8 size of one retained-response manifest.
    /// </summary>
    public const int MaxManifestUtf8Bytes = 64 * 1024;

    /// <summary>
    /// Maximum UTF-8 size of one retained-response artifact.
    /// </summary>
    public const long MaxArtifactUtf8Bytes = 1_048_576;

    /// <summary>
    /// Maximum aggregate UTF-8 size of retained-response artifacts in one workspace.
    /// </summary>
    public const long MaxWorkspaceUtf8Bytes = 67_108_864;
}
