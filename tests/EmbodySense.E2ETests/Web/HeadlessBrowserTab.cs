using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal sealed class HeadlessBrowserTab : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ClientWebSocket _socket;
    private readonly string _targetId;
    private readonly int _debugPort;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCommands = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly byte[] _buffer = new byte[65536];
    private readonly Task _readerTask;
    private readonly BrowserDevToolsReaderFailure _readerFailureState = new();
    private Exception? _readerFailure;
    private int _nextCommandId;
    private int _disposed;

    private HeadlessBrowserTab(ClientWebSocket socket, string targetId, int debugPort)
    {
        _socket = socket;
        _targetId = targetId;
        _debugPort = debugPort;
        _readerTask = ReceiveLoopAsync();
    }

    internal static async Task<HeadlessBrowserTab> StartAsync(int debugPort, string targetUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUrl);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{debugPort}"), Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Put, "/json/new?" + Uri.EscapeDataString(targetUrl));
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var target = document.RootElement;
        var targetId = target.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;
        var websocketUrl = target.TryGetProperty("webSocketDebuggerUrl", out var websocket)
            && websocket.ValueKind == JsonValueKind.String
            ? websocket.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new InvalidOperationException("The browser DevTools new-target response did not include an exact page identity.");
        }
        if (string.IsNullOrWhiteSpace(websocketUrl))
        {
            await CloseTargetAsync(debugPort, targetId).ConfigureAwait(false);
            throw new InvalidOperationException("The browser DevTools new-target response did not include an exact websocket endpoint.");
        }

        var socket = new ClientWebSocket();
        HeadlessBrowserTab? tab = null;
        try
        {
            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(new Uri(websocketUrl), startupTimeout.Token).ConfigureAwait(false);
            tab = new HeadlessBrowserTab(socket, targetId, debugPort);
            await tab.SendCommandAsync("Page.enable", cancellationToken: startupTimeout.Token).ConfigureAwait(false);
            await tab.SendCommandAsync("Runtime.enable", cancellationToken: startupTimeout.Token).ConfigureAwait(false);
            await tab.SendCommandAsync("Network.enable", cancellationToken: startupTimeout.Token).ConfigureAwait(false);
            await tab.SendCommandAsync("Page.navigate", new { url = targetUrl }, startupTimeout.Token).ConfigureAwait(false);
            return tab;
        }
        catch
        {
            try
            {
                if (tab is not null)
                {
                    await tab.DisposeAsync().ConfigureAwait(false);
                }
                else
                {
                    socket.Dispose();
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or IOException or ObjectDisposedException)
            {
            }

            await CloseTargetAsync(debugPort, targetId).ConfigureAwait(false);
            throw;
        }
    }

    internal async Task WaitForExpressionAsync(string expression)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        await BrowserReadOnlyWait.WaitForTrueAsync(token => EvaluateBooleanAsync(expression, token), TimeSpan.FromSeconds(30), "Browser Runtime.evaluate read-only wait timed out.").ConfigureAwait(false);
    }

    internal Task<string> EvaluateStringAsync(string expression, CancellationToken cancellationToken = default)
        => EvaluateAsync(expression, cancellationToken).ContinueWith(static task => task.GetAwaiter().GetResult().GetString() ?? string.Empty, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal async Task<bool> EvaluateBooleanAsync(string expression, CancellationToken cancellationToken = default)
    {
        var value = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        return value.ValueKind == JsonValueKind.True;
    }

    internal async Task<int> EvaluateInt32Async(string expression, CancellationToken cancellationToken = default)
    {
        var value = await EvaluateAsync(expression, cancellationToken).ConfigureAwait(false);
        return value.GetInt32();
    }

    internal Task EvaluateWithUserGestureAsync(string expression)
        => EvaluateAsync(expression, CancellationToken.None, userGesture: true);

    internal async Task ReloadAsync()
    {
        _ = await SendCommandAsync("Page.reload", new { ignoreCache = true }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    _ = await SendCommandAsync("Page.close", cancellationToken: timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (BrowserDevToolsReaderFailure.IsCleanupException(exception))
                {
                }

                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "tab complete", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
                {
                    _socket.Abort();
                }
            }
        }
        finally
        {
            _socket.Dispose();
            try
            {
                await _readerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or ObjectDisposedException or IOException)
            {
            }

            _sendGate.Dispose();
            await CloseTargetAsync(_debugPort, _targetId).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken, bool userGesture = false)
    {
        var response = await SendCommandAsync("Runtime.evaluate", new
        {
            expression,
            awaitPromise = true,
            returnByValue = true,
            userGesture
        }, cancellationToken).ConfigureAwait(false);
        return BrowserDevToolsResponse.ReadRuntimeEvaluationValue(response);
    }

    private async Task<JsonElement> SendCommandAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var commandTimeout = cancellationToken.CanBeCanceled
            ? null
            : new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var effectiveCancellationToken = commandTimeout?.Token ?? cancellationToken;
        using var terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(effectiveCancellationToken, _readerFailureState.TerminalCancellationToken);
        var sendCancellationToken = terminalCancellation.Token;
        var commandId = Interlocked.Increment(ref _nextCommandId);
        var payload = parameters is null
            ? JsonSerializer.Serialize(new { id = commandId, method }, _jsonOptions)
            : JsonSerializer.Serialize(new { id = commandId, method, @params = parameters }, _jsonOptions);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_readerFailureState.TryRegister(_pendingCommands, null, commandId, completion, null))
        {
            return await completion.Task.ConfigureAwait(false);
        }

        try
        {
            await _sendGate.WaitAsync(sendCancellationToken).ConfigureAwait(false);
            try
            {
                if (_readerFailureState.TerminalFailure is { } terminalFailure)
                {
                    throw terminalFailure;
                }

                await _socket.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, sendCancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendGate.Release();
            }

            var response = await completion.Task.WaitAsync(sendCancellationToken).ConfigureAwait(false);
            BrowserDevToolsResponse.Validate(method, response);
            return response;
        }
        catch (OperationCanceledException) when (effectiveCancellationToken.IsCancellationRequested)
        {
            _readerFailureState.Remove(_pendingCommands, null, commandId);
            throw;
        }
        catch
        {
            _readerFailureState.Remove(_pendingCommands, null, commandId);
            _readerFailureState.ThrowIfCancellationOrTerminal(effectiveCancellationToken);
            throw;
        }
    }

    private async Task ReceiveLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && _socket.State == WebSocketState.Open)
            {
                using var document = await ReadMessageAsync().ConfigureAwait(false);
                var root = document.RootElement;
                if (BrowserDevToolsEnvelope.TryReadCommandId(root, out var commandId))
                {
                    _readerFailureState.TryComplete(_pendingCommands, null, commandId, root);
                }
            }
        }
        catch (Exception exception) when (exception is BrowserDevToolsException or WebSocketException or IOException or InvalidOperationException or JsonException or ObjectDisposedException)
        {
            failure = exception;
            if (Volatile.Read(ref _disposed) == 0)
            {
                _readerFailure = exception;
            }
        }
        finally
        {
            var completionFailure = _readerFailureState.TransitionToTerminal(_pendingCommands, null, _readerFailure ?? failure ?? new ObjectDisposedException(nameof(HeadlessBrowserTab)));
            _readerFailure = completionFailure;
        }
    }

    private async Task<JsonDocument> ReadMessageAsync()
    {
        var builder = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(_buffer, CancellationToken.None).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("Browser tab DevTools websocket closed before the expected response arrived.");
            }

            builder.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
        }
        while (!result.EndOfMessage);

        return BrowserDevToolsEnvelope.Parse(builder.ToString());
    }

    private static async Task CloseTargetAsync(int debugPort, string targetId)
    {
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{debugPort}"), Timeout = TimeSpan.FromSeconds(2) };
            using var response = await client.GetAsync("/json/close/" + Uri.EscapeDataString(targetId)).ConfigureAwait(false);
            _ = response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
        }
    }
}
