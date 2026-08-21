using System.Text.RegularExpressions;

namespace EmbodySense.Core.Clients.Capabilities;

internal static partial class CapabilityProcessDiagnosticRedactor
{
    private const int MaximumDiagnosticCharacters = 1_024;

    public static string Redact(string value)
        => Redact(value, MaximumDiagnosticCharacters);

    public static string Redact(string value, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (maximumCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }
        var redacted = SecretPattern().Replace(value, "$1=[redacted]");
        redacted = WindowsPathPattern().Replace(redacted, "[path]");
        redacted = UnixPathPattern().Replace(redacted, "[path]");
        if (redacted.Length <= maximumCharacters)
        {
            return redacted;
        }
        var retainedCharacters = maximumCharacters;
        if (char.IsHighSurrogate(redacted[retainedCharacters - 1])
            && char.IsLowSurrogate(redacted[retainedCharacters]))
        {
            retainedCharacters--;
        }
        return redacted[..retainedCharacters];
    }

    [GeneratedRegex("(?i)\\b(token|secret|password|api[_-]?key|authorization|bearer)\\s*[:=]\\s*[^\\s,;]+|\\bBearer\\s+[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("(?i)(?:[a-z]:\\\\)[^\\r\\n\\t \\\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex("(?<![A-Za-z0-9])/(?:[^\\s/\\\"']+/)+[^\\s\\\"']*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathPattern();
}
