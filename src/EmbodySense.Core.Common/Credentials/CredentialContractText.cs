using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.Credentials;

internal static class CredentialContractText
{
    internal static bool IsToken(string? value, int maximum = CredentialContractLimits.MaxTokenCharacters)
    {
        return !string.IsNullOrEmpty(value) && value.Length <= maximum && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9' && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9' && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '/');
    }

    internal static bool IsSafeText(string? value, int maximum, bool allowEmpty = false)
    {
        if (value is null || value.Length > maximum || !allowEmpty && value.Length == 0 || !value.IsNormalized(NormalizationForm.FormC))
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
            if (category is UnicodeCategory.Format or UnicodeCategory.Control || rune.Value is >= 0xfdd0 and <= 0xfdef || (rune.Value & 0xffff) is 0xfffe or 0xffff)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsUtc(DateTimeOffset value)
    {
        return value.Offset == TimeSpan.Zero;
    }
}
