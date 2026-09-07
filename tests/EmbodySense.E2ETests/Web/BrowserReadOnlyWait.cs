namespace EmbodySense.E2ETests.Web;

internal static class BrowserReadOnlyWait
{
    public static bool IsExpectedContextTurnover(Exception exception)
    {
        return exception is BrowserDevToolsException { Method: "Runtime.evaluate", Code: -32000 } browserException
            && (browserException.Message.Contains("Cannot find context with specified id", StringComparison.Ordinal)
                || browserException.Message.Contains("Execution context was destroyed.", StringComparison.Ordinal));
    }
}
