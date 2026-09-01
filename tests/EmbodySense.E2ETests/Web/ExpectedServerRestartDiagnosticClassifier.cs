namespace EmbodySense.E2ETests.Web;

internal static class ExpectedServerRestartDiagnosticClassifier
{
    private const string BrowserSessionRecoveryDescription = "Error: The browser session is being recovered.";

    public static bool IsExpectedNetworkFailure(
        bool expectedServerRestart,
        bool beganDuringOutage,
        string? requestUrl,
        string? errorText,
        string targetAuthority,
        bool capturedAtRestart = false)
    {
        if ((!expectedServerRestart && !beganDuringOutage && !capturedAtRestart)
            || !IsTargetAuthority(requestUrl, targetAuthority))
        {
            return false;
        }

        if (capturedAtRestart)
        {
            return errorText?.Contains("ERR_CONNECTION_RESET", StringComparison.OrdinalIgnoreCase) == true;
        }

        return IsExpectedServerRestartUrl(requestUrl, targetAuthority)
            && (errorText?.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase) == true
            || errorText?.Contains("ERR_CONNECTION_RESET", StringComparison.OrdinalIgnoreCase) == true
            || errorText?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true);
    }

    public static bool IsExpectedServerRestartLogEntry(
        bool expectedServerRestart,
        bool beganDuringOutage,
        string? source,
        string? text,
        string? url,
        string? correlatedRequestUrl,
        string targetAuthority,
        bool capturedAtRestart = false)
    {
        if ((!expectedServerRestart && !beganDuringOutage && !capturedAtRestart)
            || !string.Equals(source, "network", StringComparison.Ordinal)
            || !ContainsTargetAuthority(text, targetAuthority)
                && !ContainsTargetAuthority(url, targetAuthority)
                && !ContainsTargetAuthority(correlatedRequestUrl, targetAuthority))
        {
            return false;
        }

        var isConnectionReset = text?.Contains("ERR_CONNECTION_RESET", StringComparison.OrdinalIgnoreCase) == true;
        if (capturedAtRestart)
        {
            return isConnectionReset
                && (url is null || IsTargetAuthority(url, targetAuthority))
                && (correlatedRequestUrl is null || IsTargetAuthority(correlatedRequestUrl, targetAuthority))
                && (IsTargetAuthority(url, targetAuthority) || IsTargetAuthority(correlatedRequestUrl, targetAuthority));
        }

        var expectedRoute = IsExpectedServerRestartUrl(url, targetAuthority)
            || IsExpectedServerRestartUrl(correlatedRequestUrl, targetAuthority);
        var expected = text?.Contains("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase) == true
            || (text?.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) == true || expectedRoute)
            && (text?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true
                || text?.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase) == true
                || isConnectionReset);

        return expected && (!isConnectionReset || expectedRoute);
    }

    public static bool IsExpectedServerRestartUrl(string? value, string targetAuthority)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, targetAuthority, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                || (string.Equals(uri.AbsolutePath, "/api/session", StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))));
    }

    public static bool IsExpectedServerRestartPageException(
        bool expectedServerRestart,
        string? text,
        string? className,
        string? description,
        string? functionName,
        string? url,
        string targetAuthority)
    {
        if (!expectedServerRestart
            || !string.Equals(text, "Uncaught (in promise)", StringComparison.Ordinal)
            || !string.Equals(className, "Error", StringComparison.Ordinal)
            || !string.Equals(functionName, "suspendSession", StringComparison.Ordinal)
            || !IsExpectedLoopBuilderScript(url, targetAuthority))
        {
            return false;
        }

        return description?.StartsWith(BrowserSessionRecoveryDescription + "\n", StringComparison.Ordinal) == true;
    }

    private static bool ContainsTargetAuthority(string? value, string targetAuthority)
    {
        return value?.Contains(targetAuthority, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsTargetAuthority(string? value, string targetAuthority)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, targetAuthority, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpectedLoopBuilderScript(string? value, string targetAuthority)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, targetAuthority, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            && string.Equals(uri.AbsolutePath, "/loop-builder.js", StringComparison.Ordinal);
    }
}
