using System.Text;

namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

internal static class CustomLoopRunValidationRules
{
    internal static void ValidateContentHash(string? content, string? hash, string field, List<CustomLoopValidationError> errors)
    {
        ValidateHash(hash, field, errors);
        if (content is not null && hash is not null && !CustomLoopTraceContentHash.Matches(content, hash))
        {
            Add(errors, "content_hash_mismatch", field, "Content hash does not match the exact retained content.");
        }
    }

    internal static void ValidateHash(string? hash, string field, List<CustomLoopValidationError> errors)
    {
        if (hash is not { Length: CustomLoopLimits.Sha256HexCharacters } || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            Add(errors, "invalid_sha256_hash", field, "Hash must be a 64-character lowercase SHA-256 hexadecimal value.");
        }
    }

    internal static bool IsSha256(string? hash)
    {
        return hash is { Length: CustomLoopLimits.Sha256HexCharacters } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    internal static void ValidateArtifactId(string? value, string field, List<CustomLoopValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value))
        {
            Add(errors, "invalid_artifact_id", field, "Artifact id must be a safe lowercase filename identifier.");
        }
    }

    internal static void ValidateOptionalText(string? value, string field, int maxCharacters, List<CustomLoopValidationError> errors, bool requireNormalized = true)
    {
        if (value is not null)
        {
            ValidateText(value, field, maxCharacters, required: false, errors, requireNormalized);
        }
    }

    internal static void ValidateText(string? value, string field, int maxCharacters, bool required, List<CustomLoopValidationError> errors, bool requireNormalized = true)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            Add(errors, "text_required", field, $"{field} is required.");
            return;
        }

        if (value.Length > maxCharacters)
        {
            Add(errors, "text_too_long", field, $"{field} cannot exceed {maxCharacters} characters.");
        }

        if (ContainsUnsafeCharacters(value) || requireNormalized && !value.IsNormalized(NormalizationForm.FormC))
        {
            Add(errors, "unsafe_text", field, $"{field} must use normalized valid Unicode without unsupported control characters.");
        }
    }

    internal static void ValidateActorText(string? value, string field, int maxCharacters, List<CustomLoopValidationError> errors)
    {
        if (value is null || string.IsNullOrWhiteSpace(value))
        {
            Add(errors, "text_required", field, $"{field} is required.");
            return;
        }

        if (value.Length > maxCharacters)
        {
            Add(errors, "text_too_long", field, $"{field} cannot exceed {maxCharacters} characters.");
        }

        if (ContainsUnsafeCharacters(value, allowFormattingControls: false) || !value.IsNormalized(NormalizationForm.FormC))
        {
            Add(errors, "unsafe_text", field, $"{field} must use normalized valid Unicode without control characters.");
        }
    }

    internal static bool IsRuntimeSurface(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    internal static bool IsUtcTimestamp(DateTimeOffset value)
    {
        return value != default && value.Offset == TimeSpan.Zero;
    }

    internal static void Add(List<CustomLoopValidationError> errors, string code, string field, string message)
    {
        errors.Add(new CustomLoopValidationError(code, field, message));
    }

    private static bool ContainsUnsafeCharacters(string value, bool allowFormattingControls = true)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return true;
                }

                index++;
                continue;
            }

            if (char.IsLowSurrogate(character) || char.IsControl(character) && (!allowFormattingControls || character is not '\r' and not '\n' and not '\t'))
            {
                return true;
            }
        }

        return false;
    }
}
