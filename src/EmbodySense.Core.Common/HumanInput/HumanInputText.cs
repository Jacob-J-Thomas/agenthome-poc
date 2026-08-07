using System.Buffers;
using System.Text;
using System.Globalization;

namespace EmbodySense.Core.Common.HumanInput;

/// <summary>
/// Validates canonical, display-safe human-input text without interpreting its content as instructions or authority.
/// </summary>
public static class HumanInputText
{
    /// <summary>
    /// Determines whether text is canonical Unicode and excludes control, format, private-use, unassigned, and surrogate code points.
    /// </summary>
    /// <param name="value">The text to inspect.</param>
    /// <param name="maxLength">The maximum UTF-16 character count.</param>
    /// <param name="required">Whether an empty or whitespace-only value is invalid.</param>
    /// <returns><see langword="true"/> for bounded canonical display text; otherwise, <see langword="false"/>.</returns>
    public static bool IsValid(string? value, int maxLength, bool required)
    {
        if (value is null || value.Length > maxLength || required && string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        for (var index = 0; index < value.Length;)
        {
            if (Rune.DecodeFromUtf16(value.AsSpan(index), out var rune, out var consumed) != OperationStatus.Done)
            {
                return false;
            }

            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                return false;
            }

            index += consumed;
        }

        return value.IsNormalized(NormalizationForm.FormC);
    }
}
