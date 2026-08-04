using System.Globalization;
using System.Text;

namespace EmbodySense.Core.Common.Loops.Custom.Graph;

internal static class GovernedLoopGraphRules
{
    public static void RequireId(string? value, string parameterName)
    {
        CustomLoopArtifactIdentifier.Require(value, parameterName);
    }

    public static void RequireDistinctIds(IEnumerable<string> values, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            RequireId(value, parameterName);
            if (!seen.Add(value))
            {
                throw new ArgumentException($"Identifier `{value}` is duplicated.", parameterName);
            }
        }
    }

    public static void RequireText(string? value, string parameterName, int maximumCharacters, bool required)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }

        if (value.Length > maximumCharacters)
        {
            throw new ArgumentException($"{parameterName} cannot exceed {maximumCharacters} characters.", parameterName);
        }

        if (!value.IsNormalized(NormalizationForm.FormC) || value.Contains('\r', StringComparison.Ordinal) || value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])))
        {
            throw new ArgumentException($"{parameterName} must already be NFC-normalized, use LF line endings, and have no boundary whitespace.", parameterName);
        }

        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsSurrogate(character))
            {
                if (!char.IsHighSurrogate(character) || index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    throw new ArgumentException($"{parameterName} contains invalid Unicode.", parameterName);
                }

                index++;
            }
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control && rune.Value is not '\n' and not '\t' || category is UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
            {
                throw new ArgumentException($"{parameterName} contains unsafe Unicode.", parameterName);
            }
        }
    }

    public static void RequireSha256(string? value, string parameterName)
    {
        if (value is null || value.Length != CustomLoopLimits.Sha256HexCharacters || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException("Executable hashes must be lowercase SHA-256 hexadecimal values.", parameterName);
        }
    }

    public static void RequireDefined<TEnum>(TEnum value, string parameterName) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value, CultureInfo.InvariantCulture) == 0)
        {
            throw new ArgumentException($"{parameterName} is undefined.", parameterName);
        }
    }
}
