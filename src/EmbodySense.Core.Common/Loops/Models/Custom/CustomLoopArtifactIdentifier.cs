namespace EmbodySense.Core.Common.Loops.Models.Custom;

public static class CustomLoopArtifactIdentifier
{
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
