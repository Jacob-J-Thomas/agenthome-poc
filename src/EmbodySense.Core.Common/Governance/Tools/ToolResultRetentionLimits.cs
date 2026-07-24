namespace EmbodySense.Core.Common.Governance.Tools;

public static class ToolResultRetentionLimits
{
    public const int MaxOutputCharacters = 160_000;

    public const int MaxChunkCharacters = 32_000;

    public const int MaxArtifactsPerWorkspace = 256;

    public const long MaxArtifactUtf8Bytes = 1_048_576;

    public const long MaxWorkspaceUtf8Bytes = 67_108_864;
}
