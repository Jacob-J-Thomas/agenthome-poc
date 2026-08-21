using EmbodySense.Web.Models;

namespace EmbodySense.Web.Hubs;

internal static class WebSessionTranscriptReadPolicy
{
    public static async Task<IReadOnlyList<WebTranscriptMessage>?> ReadAsync(
        Func<CancellationToken, Task<IReadOnlyList<WebTranscriptMessage>?>> readTranscriptAsync,
        CancellationToken connectionAborted)
    {
        ArgumentNullException.ThrowIfNull(readTranscriptAsync);

        try
        {
            return await readTranscriptAsync(connectionAborted);
        }
        catch (OperationCanceledException) when (connectionAborted.IsCancellationRequested)
        {
            return null;
        }
    }
}
