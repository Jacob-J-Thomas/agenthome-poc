namespace EmbodySense.Core.Common.HumanInput;

/// <summary>
/// Validates stable schema-1 human-input identifiers.
/// </summary>
public static class HumanInputIdentifier
{
    /// <summary>
    /// Determines whether a value is a canonical human-input identifier.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="maxLength">The maximum permitted character count.</param>
    /// <returns><see langword="true"/> when the value is a lowercase ASCII identifier with only letters, digits, hyphens, underscores, or periods and no leading or trailing separator; otherwise, <see langword="false"/>.</returns>
    public static bool IsValid(string? value, int maxLength = HumanInputLimits.MaxIdentifierCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || !IsPlain(value[0]) || !IsPlain(value[^1]))
        {
            return false;
        }

        return value.All(character => IsPlain(character) || character is '-' or '_' or '.');
    }

    /// <summary>
    /// Returns a validated canonical human-input identifier.
    /// </summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="parameterName">The parameter name to report when validation fails.</param>
    /// <param name="maxLength">The maximum permitted character count.</param>
    /// <returns>The original validated value without normalization.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> is not canonical.</exception>
    public static string Require(string? value, string parameterName, int maxLength = HumanInputLimits.MaxIdentifierCharacters)
    {
        if (!IsValid(value, maxLength))
        {
            throw new ArgumentException("Human-input identifiers must be bounded lowercase ASCII identifiers.", parameterName);
        }

        return value!;
    }

    private static bool IsPlain(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
