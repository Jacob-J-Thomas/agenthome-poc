using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.Triggers;

internal static class TriggerTextRules
{
    internal static bool IsToken(string? value, int maxCharacters)
    {
        return value is not null
            && value.Length is > 0
            && value.Length <= maxCharacters
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value[^1] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
    }

    internal static bool IsGovernedPayloadReference(string? value)
    {
        const string Prefix = "payload/";
        return value is not null
            && value.Length <= TriggerDeliveryLimits.MaxPayloadReferenceCharacters
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && IsToken(value[Prefix.Length..], TriggerDeliveryLimits.MaxPayloadReferenceCharacters - Prefix.Length);
    }

    internal static bool IsSafeNormalized(string? value, int maxCharacters)
    {
        if (value is null || value.Length is 0 || value.Length > maxCharacters)
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
            if (category is UnicodeCategory.Format or UnicodeCategory.Control || IsNonCharacter(rune.Value))
            {
                return false;
            }
        }

        return value.IsNormalized(NormalizationForm.FormC);
    }

    internal static bool IsSha256(string? value)
    {
        return value?.Length == TriggerDeliveryLimits.Sha256HexCharacters && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsNonCharacter(int value) => value is >= 0xfdd0 and <= 0xfdef || (value & 0xffff) is 0xfffe or 0xffff;
}
