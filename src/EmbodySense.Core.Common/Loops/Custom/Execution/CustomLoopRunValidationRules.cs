using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using System.Text;

namespace EmbodySense.Core.Common.Loops.Custom.Execution;

/// <summary>
/// Defines and applies custom loop run validation rules.
/// </summary>
internal static class CustomLoopRunValidationRules
{
    /// <summary>
    /// Validates a lowercase SHA-256 digest and, when content is present, verifies exact fixed-time content equality.
    /// </summary>
    /// <param name="content">The retained content, or <see langword="null"/> when only digest shape can be checked.</param>
    /// <param name="hash">The lowercase hexadecimal digest.</param>
    /// <param name="field">The validation field path used in reported errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    internal static void ValidateContentHash(string? content, string? hash, string field, List<CustomLoopValidationError> errors)
    {
        ValidateHash(hash, field, errors);
        if (content is not null && hash is not null && !CustomLoopTraceContentHash.Matches(content, hash))
        {
            Add(errors, "content_hash_mismatch", field, "Content hash does not match the exact retained content.");
        }
    }

    /// <summary>
    /// Validates the shape of a lowercase SHA-256 hexadecimal digest.
    /// </summary>
    /// <param name="hash">The digest to validate.</param>
    /// <param name="field">The validation field path used in reported errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    internal static void ValidateHash(string? hash, string field, List<CustomLoopValidationError> errors)
    {
        if (hash is not { Length: CustomLoopLimits.Sha256HexCharacters } || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            Add(errors, "invalid_sha256_hash", field, "Hash must be a 64-character lowercase SHA-256 hexadecimal value.");
        }
    }

    /// <summary>
    /// Determines whether the hash is SHA-256.
    /// </summary>
    /// <param name="hash">The hash.</param>
    /// <returns><see langword="true"/> when the value is exactly 64 lowercase hexadecimal characters; otherwise, <see langword="false"/>.</returns>
    internal static bool IsSha256(string? hash)
    {
        return hash is { Length: CustomLoopLimits.Sha256HexCharacters } && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// Validates the artifact ID.
    /// </summary>
    /// <param name="value">The candidate artifact identifier.</param>
    /// <param name="field">The validation field path used in reported errors.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    internal static void ValidateArtifactId(string? value, string field, List<CustomLoopValidationError> errors)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(value))
        {
            Add(errors, "invalid_artifact_id", field, "Artifact id must be a safe lowercase filename identifier.");
        }
    }

    /// <summary>
    /// Validates the optional text.
    /// </summary>
    /// <param name="value">The optional text to validate.</param>
    /// <param name="field">The validation field path used in reported errors.</param>
    /// <param name="maxCharacters">The maximum permitted UTF-16 character count.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    /// <param name="requireNormalized">Whether non-null text must use Unicode normalization form C.</param>
    internal static void ValidateOptionalText(string? value, string field, int maxCharacters, List<CustomLoopValidationError> errors, bool requireNormalized = true)
    {
        if (value is not null)
        {
            ValidateText(value, field, maxCharacters, required: false, errors, requireNormalized);
        }
    }

    /// <summary>
    /// Validates the text.
    /// </summary>
    /// <param name="value">The text to validate.</param>
    /// <param name="field">The validation field path used in reported errors.</param>
    /// <param name="maxCharacters">The maximum permitted UTF-16 character count.</param>
    /// <param name="required">Whether null, empty, or whitespace-only text is rejected.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
    /// <param name="requireNormalized">Whether text must use Unicode normalization form C.</param>
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

    /// <summary>
    /// Validates the actor text.
    /// </summary>
    /// <param name="value">The actor identity text to validate.</param>
    /// <param name="field">The validation field path used in reported errors.</param>
    /// <param name="maxCharacters">The maximum permitted UTF-16 character count.</param>
    /// <param name="errors">The collection that receives validation errors.</param>
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

    /// <summary>
    /// Determines whether the value is runtime surface.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the value is a bounded lowercase ASCII identifier containing only letters, digits, or internal hyphens; otherwise, <see langword="false"/>.</returns>
    internal static bool IsRuntimeSurface(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.Length <= CustomLoopLimits.MaxArtifactIdCharacters && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    /// <summary>
    /// Determines whether the value is UTC timestamp.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the timestamp is non-default and has a zero UTC offset; otherwise, <see langword="false"/>.</returns>
    internal static bool IsUtcTimestamp(DateTimeOffset value)
    {
        return value != default && value.Offset == TimeSpan.Zero;
    }

    /// <summary>
    /// Adds a validation error to the supplied collection.
    /// </summary>
    /// <param name="errors">The collection that receives the error.</param>
    /// <param name="code">The stable machine-readable error code.</param>
    /// <param name="field">The affected validation field path.</param>
    /// <param name="message">The human-readable diagnostic.</param>
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
