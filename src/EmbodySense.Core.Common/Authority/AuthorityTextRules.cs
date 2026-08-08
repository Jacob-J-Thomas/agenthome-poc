using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.Authority;

internal static class AuthorityTextRules
{
    internal static bool IsSafeNormalized(string? value, int maxCharacters, bool allowEmpty = false)
    {
        if (value is null || value.Length > maxCharacters || (!allowEmpty && value.Length == 0))
        {
            return false;
        }

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

        return value.IsNormalized(NormalizationForm.FormC);
    }

    internal static bool IsToken(string? value, int maxCharacters)
    {
        return value is not null
            && value.Length > 0
            && value.Length <= maxCharacters
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
    }

    private static bool IsNonCharacter(int value)
    {
        return value is >= 0xfdd0 and <= 0xfdef || (value & 0xffff) is 0xfffe or 0xffff;
    }
}
