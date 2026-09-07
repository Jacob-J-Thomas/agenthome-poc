namespace EmbodySense.E2ETests.Web;

internal static class BrowserFailureDiagnostics
{
    public static async Task TryWriteAsync(Func<Task> write)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
        catch
        {
        }
    }
}
