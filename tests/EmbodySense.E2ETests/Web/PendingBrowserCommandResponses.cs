using System.Collections.Concurrent;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal sealed class PendingBrowserCommandResponses
{
    private readonly ConcurrentDictionary<int, Action<JsonElement>> _handlers = new();

    public void Add(int commandId, Action<JsonElement> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!_handlers.TryAdd(commandId, handler))
        {
            throw new InvalidOperationException($"Browser DevTools command id {commandId} already has a response handler.");
        }
    }

    public void Remove(int commandId)
    {
        _handlers.TryRemove(commandId, out _);
    }

    public void Handle(int commandId, JsonElement response)
    {
        if (_handlers.TryRemove(commandId, out var handler))
        {
            handler(response);
        }
    }
}
