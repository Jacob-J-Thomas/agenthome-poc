using System.Collections.Concurrent;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class BrowserDevToolsReaderFailure
{
    public static void CompletePending(ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingCommands, PendingBrowserCommandResponses? responseHandlers, Exception failure)
    {
        foreach (var pending in pendingCommands.ToArray())
        {
            if (pendingCommands.TryRemove(pending.Key, out var completion))
            {
                responseHandlers?.Remove(pending.Key);
                completion.TrySetException(failure);
            }
        }
    }
}
