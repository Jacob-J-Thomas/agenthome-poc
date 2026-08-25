namespace EmbodySense.Core.Common.HumanReview;

/// <summary>Validates bounded, canonical identifiers retained by Human Review contracts.</summary>
public static class HumanReviewIdentifier
{
    /// <summary>Determines whether a value is a canonical lowercase ASCII identifier.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="maxLength">The maximum character count.</param>
    /// <returns><see langword="true"/> when the value is bounded and uses only lowercase letters, digits, hyphens, underscores, or periods without a separator at either end.</returns>
    public static bool IsValid(string? value, int maxLength = HumanReviewContractLimits.MaxIdentifierCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || !IsPlain(value[0]) || !IsPlain(value[^1]))
        {
            return false;
        }

        return value.All(character => IsPlain(character) || character is '-' or '_' or '.');
    }

    private static bool IsPlain(char character) => character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
