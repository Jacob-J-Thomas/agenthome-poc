using System.Buffers;
using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates display-safe redacted Human Review text without interpreting it as instructions, credentials, or authority.</summary>
public static class HumanReviewSafeText
{
    private static readonly string[] _secretMarkers = ["secret", "password", "passwd", "token", "credential", "authorization", "api_key", "apikey", "private key", "bearer ", "sk-", "ghp_", "xoxb-", "-----begin"];

    /// <summary>Determines whether text is canonical, bounded, display-safe, and free of obvious secret-bearing material.</summary>
    /// <param name="value">The candidate text.</param>
    /// <param name="maxLength">The maximum UTF-16 character count.</param>
    /// <param name="required">Whether empty or whitespace-only text is invalid.</param>
    /// <returns><see langword="true"/> when the text is safe for a retained redacted contract field.</returns>
    public static bool IsValid(string? value, int maxLength, bool required)
    {
        if (value is null)
        {
            return !required;
        }

        if (value.Length > maxLength || required && string.IsNullOrWhiteSpace(value) || !value.IsNormalized(NormalizationForm.FormC))
        {
            return false;
        }

        for (var index = 0; index < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done
                || Rune.IsControl(rune)
                || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                return false;
            }

            index += consumed;
        }

        return !_secretMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
