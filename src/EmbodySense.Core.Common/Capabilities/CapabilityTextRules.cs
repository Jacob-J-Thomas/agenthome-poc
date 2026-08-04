using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.Capabilities;

internal static class CapabilityTextRules
{
    internal static bool IsSafeNormalized(string? value, int maxCharacters, bool allowEmpty)
    {
        if (value is null || value.Length > maxCharacters || !allowEmpty && value.Length == 0 || !HasValidSafeUnicode(value))
        {
            return false;
        }

        return value.IsNormalized(NormalizationForm.FormC);
    }

    internal static bool IsSafeAsciiToken(string? value, int maxCharacters, bool allowEmpty = false)
    {
        return value is not null && value.Length <= maxCharacters && (allowEmpty || value.Length > 0) && value.All(character => character is >= (char)0x21 and <= (char)0x7e);
    }

    private static bool HasValidSafeUnicode(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            Rune rune;
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !Rune.TryCreate(value[index], value[index + 1], out rune))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return false;
            }
            else
            {
                rune = new Rune(value[index]);
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Format || category == UnicodeCategory.Control && rune.Value is not 0x09 and not 0x0a and not 0x0d || IsNonCharacter(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNonCharacter(int value)
    {
        return value is >= 0xfdd0 and <= 0xfdef || (value & 0xffff) is 0xfffe or 0xffff;
    }
}
