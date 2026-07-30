using EmbodySense.Core.Common.Loops.Models.Custom;
namespace EmbodySense.Core.Common.Loops.Custom;

/// <summary>
/// Validates and normalizes custom loop artifact identifiers.
/// </summary>
public static class CustomLoopArtifactIdentifier
{
    /// <summary>
    /// Determines whether a value is a filename-safe custom-loop artifact identifier.
    /// </summary>
    /// <param name="value">The candidate lowercase identifier.</param>
    /// <param name="maxLength">The maximum permitted character count.</param>
    /// <returns><see langword="true"/> when the value is non-empty, within the limit, begins and ends with an ASCII lowercase letter or digit, contains only lowercase letters, digits, hyphens, underscores, or periods, and is not a reserved Windows device name; otherwise, <see langword="false"/>.</returns>
    public static bool IsValid(string? value, int maxLength = CustomLoopLimits.MaxArtifactIdCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength || !IsPlainCharacter(value[0]) || !IsPlainCharacter(value[^1]))
        {
            return false;
        }

        if (value.Any(character => !IsPlainCharacter(character) && character is not '-' and not '_' and not '.'))
        {
            return false;
        }

        var windowsBaseName = value.Split('.', 2)[0];
        return !IsReservedWindowsDeviceName(windowsBaseName);
    }

    /// <summary>
    /// Validates and returns a custom-loop artifact identifier.
    /// </summary>
    /// <param name="value">The candidate lowercase identifier.</param>
    /// <param name="parameterName">The parameter name to report when validation fails.</param>
    /// <param name="maxLength">The maximum permitted character count.</param>
    /// <returns>The validated identifier without further normalization.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="value"/> does not satisfy <see cref="IsValid(string?, int)"/>.</exception>
    public static string Require(string? value, string parameterName, int maxLength = CustomLoopLimits.MaxArtifactIdCharacters)
    {
        if (!IsValid(value, maxLength))
        {
            throw new ArgumentException("Custom loop artifact ids must be filename-safe lowercase identifiers without reserved names or trailing separators.", parameterName);
        }

        return value!;
    }

    private static bool IsPlainCharacter(char character)
    {
        return character is >= 'a' and <= 'z' or >= '0' and <= '9';
    }

    private static bool IsReservedWindowsDeviceName(string value)
    {
        return value is "con" or "prn" or "aux" or "nul" or "clock$" || value.Length == 4 && value[..3] is "com" or "lpt" && value[3] is >= '1' and <= '9';
    }
}
