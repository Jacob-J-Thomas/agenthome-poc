using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Serializes and hydrates the canonical persisted artifact contract for a live custom-loop run.
/// </summary>
public static class CustomLoopRunArtifactSerializer
{
    public static byte[] Serialize(CustomLoopRunRecord run)
    {
        var validation = CustomLoopRunValidator.Validate(run);
        if (!validation.IsValid)
        {
            var detail = string.Join(" ", validation.Errors.Select(error => $"{error.Field}: {error.Message}"));
            throw new FormatException($"Custom loop run is invalid. {detail}");
        }

        var artifact = CustomLoopRunArtifactCodec.Encode(run);
        if (artifact.Length > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run artifact exceeds the {CustomLoopLimits.MaxRunTraceUtf8Bytes}-byte trace limit.");
        }

        return artifact;
    }

    public static CustomLoopRunRecord Deserialize(byte[] artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Length is < 1 or > CustomLoopLimits.MaxRunTraceUtf8Bytes)
        {
            throw new FormatException($"Custom loop run artifact must contain between 1 and {CustomLoopLimits.MaxRunTraceUtf8Bytes} UTF-8 bytes.");
        }

        return CustomLoopRunArtifactCodec.Decode(artifact);
    }
}
