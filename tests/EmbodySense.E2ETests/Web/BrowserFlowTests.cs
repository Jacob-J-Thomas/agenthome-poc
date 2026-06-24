using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using Microsoft.AspNetCore.Builder;

namespace EmbodySense.E2ETests.Web;

public sealed class BrowserFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Headless_browser_initializes_workspace_and_restores_history()
    {
        using var workspace = new TestWorkspace();
        await using var app = CreateApp(workspace.RootPath, out var options);
        await app.StartAsync();
        await using var browser = await HeadlessBrowserSession.StartAsync(options.Url);

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Needs initialization')");
            await browser.EvaluateAsync("document.getElementById('initButton').click()");
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await WriteCurrentTranscriptAsync(workspace, "browser restored prompt", "browser restored answer");

            await SubmitMessageAsync(browser, "/history");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('Stored conversations:')");
            await SubmitMessageAsync(browser, "1");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser restored answer')");

            var transcriptText = await browser.EvaluateStringAsync("document.getElementById('transcript').textContent");
            var workspaceStatus = await browser.EvaluateStringAsync("document.getElementById('workspaceStatus').textContent");

            Assert.Contains("Initialized", workspaceStatus);
            Assert.Contains("browser restored prompt", transcriptText);
            Assert.Contains("browser restored answer", transcriptText);
            Assert.Contains("Loaded conversation `archive/", transcriptText);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task SubmitMessageAsync(HeadlessBrowserSession browser, string message)
    {
        var jsonMessage = JsonSerializer.Serialize(message);
        await browser.EvaluateAsync("(() => { const input = document.getElementById('messageInput'); input.value = " + jsonMessage + "; document.getElementById('messageForm').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); })()");
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

    private static async Task WriteCurrentTranscriptAsync(TestWorkspace workspace, string prompt, string answer)
    {
        var path = workspace.File(".agent", "memory", "conversations", "current.ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var first = new ConversationEntry(1, "current", 1, DateTimeOffset.Parse("2026-06-01T00:01:00+00:00", CultureInfo.InvariantCulture), "user", prompt);
        var second = new ConversationEntry(1, "current", 2, DateTimeOffset.Parse("2026-06-01T00:02:00+00:00", CultureInfo.InvariantCulture), "assistant", answer);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(first, JsonOptions) + Environment.NewLine + JsonSerializer.Serialize(second, JsonOptions) + Environment.NewLine);
    }

    private sealed record ConversationEntry(int SchemaVersion, string ConversationId, int Sequence, DateTimeOffset TimestampUtc, string Role, string Content);

    private sealed class HeadlessBrowserSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ClientWebSocket _socket;
        private readonly string _userDataDirectory;
        private readonly byte[] _buffer = new byte[65536];
        private int _nextCommandId;

        private HeadlessBrowserSession(Process process, ClientWebSocket socket, string userDataDirectory)
        {
            _process = process;
            _socket = socket;
            _userDataDirectory = userDataDirectory;
        }

        public static async Task<HeadlessBrowserSession> StartAsync(string targetUrl)
        {
            var executablePath = FindBrowserExecutable();
            var debugPort = GetFreePort();
            var userDataDirectory = Path.Combine(Path.GetTempPath(), "embodysense-browser-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataDirectory);
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "--headless=new",
                    "--disable-gpu",
                    "--no-first-run",
                    "--disable-default-apps",
                    "--remote-debugging-port=" + debugPort.ToString(CultureInfo.InvariantCulture),
                    "--user-data-dir=" + userDataDirectory,
                    "about:blank"
                }
            }) ?? throw new InvalidOperationException("Headless browser process did not start.");

            try
            {
                var websocketUrl = await CreatePageAsync(debugPort, targetUrl);
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(websocketUrl), CancellationToken.None);
                var session = new HeadlessBrowserSession(process, socket, userDataDirectory);
                await session.SendCommandAsync("Page.enable");
                await session.SendCommandAsync("Runtime.enable");
                return session;
            }
            catch
            {
                await StopProcessAsync(process);
                TryDeleteDirectory(userDataDirectory);
                throw;
            }
        }

        public async Task WaitForExpressionAsync(string expression)
        {
            Exception? lastException = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    var value = await EvaluateAsync($"Boolean({expression})", timeout.Token);
                    if (value.ValueKind == JsonValueKind.True)
                    {
                        return;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or JsonException)
                {
                    lastException = exception;
                }

                await Task.Delay(100, CancellationToken.None);
            }

            throw new TimeoutException($"Browser expression did not become true: {expression}", lastException);
        }

        public async Task EvaluateAsync(string expression)
        {
            _ = await EvaluateAsync(expression, CancellationToken.None);
        }

        public async Task<string> EvaluateStringAsync(string expression)
        {
            var value = await EvaluateAsync(expression, CancellationToken.None);
            return value.GetString() ?? "";
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", CancellationToken.None);
            }

            _socket.Dispose();
            await StopProcessAsync(_process);
            TryDeleteDirectory(_userDataDirectory);
        }

        private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken)
        {
            var response = await SendCommandAsync("Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true
            }, cancellationToken);
            if (response.TryGetProperty("exceptionDetails", out var exceptionDetails))
            {
                throw new InvalidOperationException("Browser evaluation failed: " + exceptionDetails.GetRawText());
            }

            var remoteObject = response.GetProperty("result").GetProperty("result");
            return remoteObject.TryGetProperty("value", out var value) ? value.Clone() : default;
        }

        private async Task<JsonElement> SendCommandAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        {
            var commandId = Interlocked.Increment(ref _nextCommandId);
            var payload = parameters is null
                ? JsonSerializer.Serialize(new { id = commandId, method }, JsonOptions)
                : JsonSerializer.Serialize(new { id = commandId, method, @params = parameters }, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(payload);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);

            while (true)
            {
                using var document = await ReadMessageAsync(cancellationToken);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id) && id.GetInt32() == commandId)
                {
                    return root.Clone();
                }
            }
        }

        private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
        {
            var builder = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(_buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("Browser DevTools websocket closed before the expected response arrived.");
                }

                builder.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            return JsonDocument.Parse(builder.ToString());
        }

        private static async Task<string> CreatePageAsync(int debugPort, string targetUrl)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{debugPort}") };
            await WaitForDevToolsAsync(client);
            using var request = new HttpRequestMessage(HttpMethod.Put, "/json/new?" + Uri.EscapeDataString(targetUrl));
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.GetProperty("webSocketDebuggerUrl").GetString()
                ?? throw new InvalidOperationException("Browser DevTools target did not include a websocket URL.");
        }

        private static async Task WaitForDevToolsAsync(HttpClient client)
        {
            Exception? lastException = null;
            for (var i = 0; i < 50; i++)
            {
                try
                {
                    using var response = await client.GetAsync("/json/version");
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    lastException = exception;
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Headless browser DevTools endpoint did not become available.", lastException);
        }

        private static string FindBrowserExecutable()
        {
            foreach (var candidate in new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"
            })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("Headless browser e2e requires Microsoft Edge or Google Chrome on this machine.");
        }

        private static async Task StopProcessAsync(Process process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }

            process.Dispose();
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
