namespace EmbodySense.Core.Common.Capabilities;

internal static class CapabilityIdentifierRules
{
    internal static bool IsProviderId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > CapabilityContractLimits.MaxProviderIdCharacters || value[0] == '.' || value[^1] == '.')
        {
            return false;
        }

        var labels = value.Split('.');
        return labels.Length >= 2 && labels.All(IsDnsLabel);
    }

    internal static bool IsPath(string? value, int maxCharacters)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxCharacters || value[0] == '/' || value[^1] == '/')
        {
            return false;
        }

        var segments = value.Split('/');
        return segments.Length <= 8 && segments.All(segment => IsToken(segment));
    }

    internal static bool IsToken(string? value, int maxCharacters = 63)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxCharacters || !IsAlphaNumeric(value[0]) || !IsAlphaNumeric(value[^1]))
        {
            return false;
        }

        return value.All(character => IsAlphaNumeric(character) || character is '-' or '_' or '.');
    }

    internal static bool IsHost(string? value)
    {
        return IsProviderId(value) || string.Equals(value, "localhost", StringComparison.Ordinal);
    }

    private static bool IsDnsLabel(string value)
    {
        return value.Length is >= 1 and <= 63 && IsAlphaNumeric(value[0]) && IsAlphaNumeric(value[^1]) && value.All(character => IsAlphaNumeric(character) || character == '-');
    }

    private static bool IsAlphaNumeric(char character)
    {
        return character is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}
