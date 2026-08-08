using System.Text.RegularExpressions;

namespace EmbodySense.Core.Clients.Capabilities;

internal static partial class CapabilityProcessDiagnosticRedactor
{
    private const int MaximumDiagnosticCharacters = 1_024;

    public static string Redact(string value)
    {
        var redacted = SecretPattern().Replace(value, "$1=[redacted]");
        redacted = WindowsPathPattern().Replace(redacted, "[path]");
        redacted = UnixPathPattern().Replace(redacted, "[path]");
        return redacted.Length <= MaximumDiagnosticCharacters ? redacted : redacted[..MaximumDiagnosticCharacters];
    }

    [GeneratedRegex("(?i)\\b(token|secret|password|api[_-]?key|authorization|bearer)\\s*[:=]\\s*[^\\s,;]+|\\bBearer\\s+[^\\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    [GeneratedRegex("(?i)(?:[a-z]:\\\\)[^\\r\\n\\t \\\"']+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPathPattern();

    [GeneratedRegex("(?<![A-Za-z0-9])/(?:[^\\s/\\\"']+/)+[^\\s\\\"']*", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathPattern();
}
