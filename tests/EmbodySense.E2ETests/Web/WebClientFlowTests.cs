using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Models;
using Microsoft.AspNetCore.Builder;

namespace EmbodySense.E2ETests.Web;

public sealed class WebClientFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Localhost_web_client_serves_assets_and_bootstrap_endpoints()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var index = await client.GetStringAsync("/");
            var script = await client.GetStringAsync("/app.js");
            var session = await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions);
            var status = await client.GetFromJsonAsync<WebStatus>("/api/status", JsonOptions);
            var rejectedConfig = await client.GetAsync("/api/configuration");

            Assert.Contains("EmbodySense", index);
            Assert.Contains("JsonSignalRConnection", script);
            Assert.False(string.IsNullOrWhiteSpace(session!.Token));
            Assert.False(status!.Initialized);
            Assert.Equal("web", status.Client);
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedConfig.StatusCode);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Signalr_browser_flow_initializes_workspace_and_loads_history_without_model_inference()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(options.Url) };
            var session = await client.GetFromJsonAsync<WebSessionInfo>("/api/session", JsonOptions);
            await using var signalr = await SignalRTestClient.ConnectAsync(options.Url, session!.Token);

            var initializeMessages = await signalr.InvokeAndCollectAsync("InitializeWorkspace");
            var initializeResult = Deserialize<WebStatus>(GetCompletionResult(initializeMessages));
            Assert.True(initializeResult.Initialized);
            Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));

            await WriteCurrentTranscriptAsync(workspace, "e2e restored prompt", "e2e restored answer");

            var historyMessages = await signalr.InvokeAndCollectAsync("SendMessage", "/history");
            var historyEvent = Assert.Single(GetStreamEvents(historyMessages));
            Assert.Equal("assistant_final", historyEvent.GetProperty("type").GetString());
            Assert.Contains("Stored conversations:", historyEvent.GetProperty("text").GetString());
            Assert.Contains("Send conversation number to load", historyEvent.GetProperty("text").GetString());

            var loadMessages = await signalr.InvokeAndCollectAsync("SendMessage", "1");
            var streamEvents = GetStreamEvents(loadMessages).ToArray();
            var loadedEvent = Assert.Single(streamEvents, streamEvent => streamEvent.GetProperty("type").GetString() == "history_loaded");
            var confirmationEvent = Assert.Single(streamEvents, streamEvent => streamEvent.GetProperty("type").GetString() == "assistant_final");
            var loadedMessages = loadedEvent.GetProperty("messages").EnumerateArray().ToArray();

            Assert.Collection(
                loadedMessages,
                message =>
                {
                    Assert.Equal("user", message.GetProperty("role").GetString());
                    Assert.Equal("e2e restored prompt", message.GetProperty("content").GetString());
                },
                message =>
                {
                    Assert.Equal("assistant", message.GetProperty("role").GetString());
                    Assert.Equal("e2e restored answer", message.GetProperty("content").GetString());
                });
            Assert.Contains("Loaded conversation `archive/", confirmationEvent.GetProperty("text").GetString());
            Assert.Contains("e2e restored prompt", await File.ReadAllTextAsync(workspace.File(".agent", "memory", "conversations", "current.ndjson")));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Direct_websocket_rejects_missing_or_invalid_session_token()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();

        try
        {
            await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectWebSocketAsync(options.Url, null));
            await Assert.ThrowsAnyAsync<WebSocketException>(() => ConnectWebSocketAsync(options.Url, "wrong-token"));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task Web_entrypoint_starts_as_external_process_and_serves_status()
    {
        using var workspace = new TestWorkspace();
        var port = GetFreePort();
        using var process = StartWebProcess(workspace.RootPath, port);

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            var status = await WaitForStatusAsync(client, process);

            Assert.False(status.Initialized);
            Assert.Equal("web", status.Client);
        }
        finally
        {
            await StopProcessAsync(process);
        }
    }

    private static WebApplication CreateApp(string rootPath, out WebRunOptions options)
    {
        var port = GetFreePort();
        var portText = port.ToString(CultureInfo.InvariantCulture);
        var args = new[] { "--workdir", rootPath, "--port", portText };
        options = WebRunOptions.FromArguments(args);
        var builder = Program.CreateBuilder(args, options);
        var app = builder.Build();
        Program.ConfigurePipeline(app);
        return app;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task ConnectWebSocketAsync(string baseUrl, string? sessionToken)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(CreateHubUri(baseUrl, sessionToken), CancellationToken.None);
    }

    private static WebProcess StartWebProcess(string rootPath, int port)
    {
        var webAssemblyPath = Path.Combine(AppContext.BaseDirectory, "EmbodySense.Web.dll");
        Assert.True(File.Exists(webAssemblyPath), $"Expected Web assembly at {webAssemblyPath}.");
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                webAssemblyPath,
                "--workdir",
                rootPath,
                "--port",
                port.ToString(CultureInfo.InvariantCulture)
            }
        });
        process = process ?? throw new InvalidOperationException("Web process did not start.");
        var output = new BoundedProcessOutput();
        var error = new BoundedProcessOutput();
        process.OutputDataReceived += (_, args) => output.Append(args.Data);
        process.ErrorDataReceived += (_, args) => error.Append(args.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new WebProcess(process, output, error);
    }

    private static async Task<WebStatus> WaitForStatusAsync(HttpClient client, WebProcess process)
    {
        Exception? lastException = null;
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var status = await client.GetFromJsonAsync<WebStatus>("/api/status", JsonOptions);
                return status ?? throw new InvalidOperationException("Status response was empty.");
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastException = exception;
                await Task.Delay(100);
            }
        }

        throw new TimeoutException("Web process did not serve /api/status." + Environment.NewLine + process.FormatOutput(), lastException);
    }

    private static async Task StopProcessAsync(WebProcess process)
    {
        if (process.Process.HasExited)
        {
            return;
        }

        process.Process.Kill(entireProcessTree: true);
        await process.Process.WaitForExitAsync();
    }

    private static async Task WriteCurrentTranscriptAsync(TestWorkspace workspace, string prompt, string answer)
    {
        var path = workspace.File(".agent", "memory", "conversations", "current.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var first = new ConversationEntry(1, "current", 1, DateTimeOffset.Parse("2026-06-01T00:01:00+00:00", CultureInfo.InvariantCulture), "user", prompt);
        var second = new ConversationEntry(1, "current", 2, DateTimeOffset.Parse("2026-06-01T00:02:00+00:00", CultureInfo.InvariantCulture), "assistant", answer);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(first, JsonOptions) + Environment.NewLine + JsonSerializer.Serialize(second, JsonOptions) + Environment.NewLine);
    }

    private static IReadOnlyList<JsonElement> GetStreamEvents(IReadOnlyList<JsonElement> messages)
    {
        return messages
            .Where(message => message.TryGetProperty("type", out var type) && type.GetInt32() == 1)
            .Where(message => message.TryGetProperty("target", out var target) && target.GetString() == "StreamEvent")
            .Select(message => message.GetProperty("arguments")[0].Clone())
            .ToArray();
    }

    private static JsonElement GetCompletionResult(IReadOnlyList<JsonElement> messages)
    {
        var completion = Assert.Single(messages, message => message.TryGetProperty("type", out var type) && type.GetInt32() == 3);
        return completion.GetProperty("result").Clone();
    }

    private static T Deserialize<T>(JsonElement element)
    {
        return JsonSerializer.Deserialize<T>(element.GetRawText(), JsonOptions) ?? throw new InvalidOperationException($"Could not deserialize {typeof(T).Name}.");
    }

    private sealed record ConversationEntry(int SchemaVersion, string ConversationId, int Sequence, DateTimeOffset TimestampUtc, string Role, string Content);

    private sealed class WebProcess : IDisposable
    {
        private readonly BoundedProcessOutput _output;
        private readonly BoundedProcessOutput _error;

        public WebProcess(Process process, BoundedProcessOutput output, BoundedProcessOutput error)
        {
            Process = process;
            _output = output;
            _error = error;
        }

        public Process Process { get; }

        public string FormatOutput()
        {
            return "stdout:" + Environment.NewLine + _output.Text + Environment.NewLine + "stderr:" + Environment.NewLine + _error.Text;
        }

        public void Dispose()
        {
            Process.Dispose();
        }
    }

    private sealed class BoundedProcessOutput
    {
        private const int MaxCharacters = 16_000;
        private readonly StringBuilder _builder = new();

        public string Text
        {
            get
            {
                lock (_builder)
                {
                    return _builder.ToString();
                }
            }
        }

        public void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (_builder)
            {
                _builder.AppendLine(line);
                if (_builder.Length > MaxCharacters)
                {
                    _builder.Remove(0, _builder.Length - MaxCharacters);
                }
            }
        }
    }

    private sealed class SignalRTestClient : IAsyncDisposable
    {
        private const char RecordSeparator = '\u001e';
        private readonly ClientWebSocket _socket = new();
        private readonly byte[] _buffer = new byte[8192];
        private readonly Queue<string> _frames = new();
        private readonly Queue<JsonElement> _pendingMessages = new();
        private readonly StringBuilder _incoming = new();
        private int _nextInvocationId;

        public static async Task<SignalRTestClient> ConnectAsync(string baseUrl, string sessionToken)
        {
            var client = new SignalRTestClient();
            await client._socket.ConnectAsync(CreateHubUri(baseUrl, sessionToken), CancellationToken.None);
            await client.SendRawAsync(new { protocol = "json", version = 1 }, CancellationToken.None);
            await client.WaitForHandshakeAsync();
            return client;
        }

        public async Task<IReadOnlyList<JsonElement>> InvokeAndCollectAsync(string target, params object?[] arguments)
        {
            var invocationId = (_nextInvocationId++).ToString(CultureInfo.InvariantCulture);
            await SendRawAsync(new { type = 1, invocationId, target, arguments }, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var messages = new List<JsonElement>();
            while (true)
            {
                var message = await ReadMessageAsync(timeout.Token);
                messages.Add(message);
                if (IsCompletionFor(message, invocationId))
                {
                    return messages;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
            }

            _socket.Dispose();
        }

        private async Task WaitForHandshakeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await ReadMessageAsync(timeout.Token);
                if (!message.TryGetProperty("type", out _))
                {
                    if (message.TryGetProperty("error", out var error))
                    {
                        throw new InvalidOperationException(error.GetString());
                    }

                    return;
                }

                _pendingMessages.Enqueue(message.Clone());
            }
        }

        private async Task SendRawAsync(object payload, CancellationToken cancellationToken)
        {
            var text = JsonSerializer.Serialize(payload, JsonOptions) + RecordSeparator;
            var bytes = Encoding.UTF8.GetBytes(text);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        private async Task<JsonElement> ReadMessageAsync(CancellationToken cancellationToken)
        {
            if (_pendingMessages.Count > 0)
            {
                return _pendingMessages.Dequeue();
            }

            while (_frames.Count == 0)
            {
                var result = await _socket.ReceiveAsync(_buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("SignalR test websocket closed before the expected message arrived.");
                }

                _incoming.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
                ExtractFrames();
            }

            using var document = JsonDocument.Parse(_frames.Dequeue());
            return document.RootElement.Clone();
        }

        private void ExtractFrames()
        {
            var text = _incoming.ToString();
            var separatorIndex = text.IndexOf(RecordSeparator);
            if (separatorIndex < 0)
            {
                return;
            }

            _incoming.Clear();
            var start = 0;
            while (separatorIndex >= 0)
            {
                var frame = text[start..separatorIndex];
                if (!string.IsNullOrWhiteSpace(frame))
                {
                    _frames.Enqueue(frame);
                }

                start = separatorIndex + 1;
                separatorIndex = text.IndexOf(RecordSeparator, start);
            }

            _incoming.Append(text[start..]);
        }

        private static Uri CreateHubUri(string baseUrl, string sessionToken)
        {
            return WebClientFlowTests.CreateHubUri(baseUrl, sessionToken);
        }

        private static bool IsCompletionFor(JsonElement message, string invocationId)
        {
            return message.TryGetProperty("type", out var type)
                && type.GetInt32() == 3
                && message.TryGetProperty("invocationId", out var id)
                && id.GetString() == invocationId;
        }
    }

    private static Uri CreateHubUri(string baseUrl, string? sessionToken)
    {
        var baseUri = new Uri(baseUrl);
        var builder = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
            Path = "/hubs/session",
            Query = string.IsNullOrEmpty(sessionToken) ? "" : "access_token=" + Uri.EscapeDataString(sessionToken)
        };
        return builder.Uri;
    }
}
