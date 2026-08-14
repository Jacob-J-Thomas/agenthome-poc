using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.Loops.PureNodes;

internal static class GovernedLoopPureNodeTextRules
{
    public static bool IsSafe(string? value, int maximumCharacters)
    {
        if (value is null || value.Length > maximumCharacters || !value.IsNormalized(NormalizationForm.FormC) || value.Contains('\r', StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }

            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }

            index++;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control && rune.Value is not '\n' and not '\t' || category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                return false;
            }
        }

        return true;
    }
}
