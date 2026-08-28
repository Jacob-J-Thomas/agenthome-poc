using EmbodySense.Core.Common.HumanInput;

namespace EmbodySense.Core.Common.Loops.HumanInput.Policies;

/// <summary>Rejects secret-bearing or authority-bearing text from the closed schema-1 Human Input policy identity surface.</summary>
public static class HumanInputPolicyTextSafety
{
    private static readonly string[] _unsafeMarkers =
    [
        "secret", "password", "passwd", "pwd", "token", "credential", "api-key", "api_key", "apikey", "api-token", "api_token", "authorization", "bearer", "access-token", "access_token", "private-key", "private_key", "client-secret", "client_secret",
        "approve", "approval", "reject", "review", "authority", "grant", "authorize"
    ];
    private static readonly string[] _secretTokenPrefixes = ["ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_", "xoxb-", "xoxp-", "xoxa-", "xoxr-", "xoxs-", "xapp-", "sk-", "rk-"];

    /// <summary>Gets whether a text value is a canonical identifier without secret material or authority semantics.</summary>
    /// <param name="value">The untrusted text value.</param>
    /// <returns><see langword="true"/> only when the bounded identifier contains no prohibited marker.</returns>
    public static bool IsSafeIdentifier(string? value)
        => HumanInputIdentifier.IsValid(value) && !ContainsUnsafeMarker(value!);

    /// <summary>Gets whether text contains a prohibited secret or authority marker.</summary>
    /// <param name="value">The untrusted text value.</param>
    /// <returns><see langword="true"/> when the text must not be persisted as a Human Input policy identity or scope.</returns>
    public static bool ContainsUnsafeMarker(string? value)
        => !string.IsNullOrEmpty(value)
            && (_unsafeMarkers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase))
                || _secretTokenPrefixes.Any(marker => ContainsTokenPrefix(value, marker)));

    private static bool ContainsTokenPrefix(string value, string marker)
    {
        var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index >= 0 && (index == 0 || value[index - 1] is '-' or '_' or '.');
    }
}
