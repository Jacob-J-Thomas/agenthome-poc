using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserDevToolsReaderFailure
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _terminalCancellation = new();
    private Exception? _terminalFailure;

    public CancellationToken TerminalCancellationToken => _terminalCancellation.Token;

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

    public void ThrowIfCancellationOrTerminal(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTerminal();
    }

    public static bool IsCleanupException(Exception exception)
    {
        return exception is BrowserDevToolsException or OperationCanceledException or WebSocketException or IOException or InvalidOperationException or ObjectDisposedException;
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
        Exception terminalFailure;
        var cancelTerminalSignal = false;
        lock (_gate)
        {
            if (_terminalFailure is null)
            {
                _terminalFailure = failure;
                cancelTerminalSignal = true;
            }

            foreach (var pending in pendingCommands.ToArray())
            {
                if (pendingCommands.TryRemove(pending.Key, out var completion))
                {
                    responseHandlers?.Remove(pending.Key);
                    completion.TrySetException(_terminalFailure);
                }
            }

            terminalFailure = _terminalFailure;
        }

        if (cancelTerminalSignal)
        {
            _terminalCancellation.Cancel();
        }

        return terminalFailure;
    }
}
