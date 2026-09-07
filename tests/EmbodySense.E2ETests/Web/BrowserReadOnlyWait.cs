namespace EmbodySense.E2ETests.Web;

internal static class BrowserReadOnlyWait
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(100);

    public static bool IsExpectedContextTurnover(Exception exception)
    {
        return exception is BrowserDevToolsException { Method: "Runtime.evaluate", Code: -32000 } browserException
            && (string.Equals(browserException.SafeMessage, "Cannot find context with specified id", StringComparison.Ordinal)
                || string.Equals(browserException.SafeMessage, "Execution context was destroyed.", StringComparison.Ordinal));
    }

    public static async Task WaitForTrueAsync(Func<CancellationToken, Task<bool>> read, TimeSpan timeoutValue, string timeoutMessage)
    {
        using var timeout = new CancellationTokenSource(timeoutValue);
        Exception? lastException = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                if (await read(timeout.Token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (IsExpectedContextTurnover(exception))
            {
                lastException = exception;
            }

            try
            {
                await Task.Delay(_retryDelay, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException(timeoutMessage, lastException);
    }
}
