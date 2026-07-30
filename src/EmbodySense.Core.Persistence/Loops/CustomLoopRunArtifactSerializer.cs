using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Serializes and hydrates the canonical persisted artifact contract for a live custom-loop run.
/// </summary>
/// <remarks>
/// Serialization validates the complete run and the encoded byte limit before returning content. Deserialization accepts only
/// the current compact version-1 envelope through <see cref="CustomLoopRunArtifactCodec"/>; malformed, unsupported, oversized,
/// or semantically invalid artifacts throw <see cref="FormatException"/>.
/// </remarks>
public static class CustomLoopRunArtifactSerializer
{
    /// <summary>
    /// Validates and encodes a run into the bounded canonical version-1 artifact representation.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The compact persisted artifact bytes.</returns>
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

    /// <summary>
    /// Decodes and validates a bounded canonical version-1 run artifact.
    /// </summary>
    /// <param name="artifact">The artifact.</param>
    /// <returns>The hydrated custom-loop run record.</returns>
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
