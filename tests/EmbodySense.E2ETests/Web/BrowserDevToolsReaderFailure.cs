using System.Collections.Concurrent;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserDevToolsReaderFailure
{
    private readonly object _gate = new();
    private Exception? _terminalFailure;

    public Exception? TerminalFailure
    {
        get
        {
            lock (_gate)
            {
                return _terminalFailure;
            }
        }
    }

    public void ThrowIfTerminal()
    {
        if (TerminalFailure is { } terminalFailure)
        {
            throw terminalFailure;
        }
    }

    public bool TryRegister(ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingCommands, PendingBrowserCommandResponses? responseHandlers, int commandId, TaskCompletionSource<JsonElement> completion, Action<JsonElement>? responseHandler)
    {
        lock (_gate)
        {
            if (_terminalFailure is not null)
            {
                completion.TrySetException(_terminalFailure);
                return false;
            }

            if (!pendingCommands.TryAdd(commandId, completion))
            {
                throw new InvalidOperationException($"Browser DevTools command id {commandId} was already pending.");
            }

            try
            {
                if (responseHandler is not null)
                {
                    responseHandlers!.Add(commandId, responseHandler);
                }
            }
            catch
            {
                pendingCommands.TryRemove(commandId, out _);
                throw;
            }

            return true;
        }
    }

    public bool TryComplete(ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingCommands, PendingBrowserCommandResponses? responseHandlers, int commandId, JsonElement response)
    {
        lock (_gate)
        {
            if (_terminalFailure is not null || !pendingCommands.TryRemove(commandId, out var completion))
            {
                return false;
            }

            try
            {
                responseHandlers?.Handle(commandId, response);
                completion.TrySetResult(response.Clone());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }

            return true;
        }
    }

    public void Remove(ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingCommands, PendingBrowserCommandResponses? responseHandlers, int commandId)
    {
        lock (_gate)
        {
            pendingCommands.TryRemove(commandId, out _);
            responseHandlers?.Remove(commandId);
        }
    }

    public Exception TransitionToTerminal(ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pendingCommands, PendingBrowserCommandResponses? responseHandlers, Exception failure)
    {
        lock (_gate)
        {
            _terminalFailure ??= failure;
            foreach (var pending in pendingCommands.ToArray())
            {
                if (pendingCommands.TryRemove(pending.Key, out var completion))
                {
                    responseHandlers?.Remove(pending.Key);
                    completion.TrySetException(_terminalFailure);
                }
            }

            return _terminalFailure;
        }
    }
}
