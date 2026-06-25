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

    [InstalledBrowserFact]
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

    private sealed class InstalledBrowserFactAttribute : FactAttribute
    {
        public InstalledBrowserFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("EMBODYSENSE_RUN_BROWSER_E2E") != "1")
            {
                Skip = "Installed-browser E2E is opt-in because local Edge/Chrome GPU startup can be host-specific; set EMBODYSENSE_RUN_BROWSER_E2E=1 to run it.";
            }
        }
    }

    private sealed class HeadlessBrowserSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ClientWebSocket _socket;
        private readonly string _userDataDirectory;
        private readonly BoundedProcessOutput _output;
        private readonly BoundedProcessOutput _error;
        private readonly byte[] _buffer = new byte[65536];
        private int _nextCommandId;

        private HeadlessBrowserSession(Process process, ClientWebSocket socket, string userDataDirectory, BoundedProcessOutput output, BoundedProcessOutput error)
        {
            _process = process;
            _socket = socket;
            _userDataDirectory = userDataDirectory;
            _output = output;
            _error = error;
        }

        public static async Task<HeadlessBrowserSession> StartAsync(string targetUrl)
        {
            var executablePath = FindBrowserExecutable();
            Exception? lastException = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    return await StartAttemptAsync(executablePath, targetUrl);
                }
                catch (InvalidOperationException exception)
                {
                    lastException = exception;
                    await Task.Delay(250);
                }
            }

            throw new InvalidOperationException("Headless browser startup failed after 3 attempts.", lastException);
        }

        private static async Task<HeadlessBrowserSession> StartAttemptAsync(string executablePath, string targetUrl)
        {
            var debugPort = GetFreePort();
            var userDataDirectory = Path.Combine(Path.GetTempPath(), "embodysense-browser-e2e-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(userDataDirectory);
            var output = new BoundedProcessOutput();
            var error = new BoundedProcessOutput();
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "--headless=new",
                    "--disable-gpu",
                    "--disable-gpu-compositing",
                    "--disable-accelerated-2d-canvas",
                    "--disable-accelerated-video-decode",
                    "--disable-features=CanvasOopRasterization,DawnGraphite,SkiaGraphite,UseDawn,UseSkiaRenderer,Vulkan",
                    "--no-first-run",
                    "--disable-default-apps",
                    "--disable-background-networking",
                    "--disable-dev-shm-usage",
                    "--no-default-browser-check",
                    "--remote-debugging-port=" + debugPort.ToString(CultureInfo.InvariantCulture),
                    "--user-data-dir=" + userDataDirectory,
                    "about:blank"
                }
            }) ?? throw new InvalidOperationException("Headless browser process did not start.");
            process.OutputDataReceived += (_, args) => output.Append(args.Data);
            process.ErrorDataReceived += (_, args) => error.Append(args.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                var websocketUrl = await GetInitialPageWebSocketUrlAsync(debugPort);
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(websocketUrl), CancellationToken.None);
                var session = new HeadlessBrowserSession(process, socket, userDataDirectory, output, error);
                await session.SendCommandAsync("Page.enable");
                await session.SendCommandAsync("Runtime.enable");
                await session.SendCommandAsync("Page.navigate", new { url = targetUrl });
                return session;
            }
            catch (Exception exception)
            {
                await StopProcessAsync(process);
                TryDeleteDirectory(userDataDirectory);
                throw new InvalidOperationException("Headless browser startup failed." + Environment.NewLine + FormatOutput(output, error), exception);
            }
        }

        public async Task WaitForExpressionAsync(string expression)
        {
            Exception? lastException = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException)
            {
                throw new InvalidOperationException("Browser DevTools command send failed." + Environment.NewLine + FormatOutput(), exception);
            }

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
                try
                {
                    result = await _socket.ReceiveAsync(_buffer, cancellationToken);
                }
                catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException)
                {
                    throw new InvalidOperationException("Browser DevTools command receive failed." + Environment.NewLine + FormatOutput(), exception);
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException("Browser DevTools websocket closed before the expected response arrived." + Environment.NewLine + FormatOutput());
                }

                builder.Append(Encoding.UTF8.GetString(_buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            return JsonDocument.Parse(builder.ToString());
        }

        private string FormatOutput()
        {
            return FormatOutput(_output, _error);
        }

        private static string FormatOutput(BoundedProcessOutput output, BoundedProcessOutput error)
        {
            return "browser stdout:" + Environment.NewLine + output.Text + Environment.NewLine + "browser stderr:" + Environment.NewLine + error.Text;
        }

        private static async Task<string> GetInitialPageWebSocketUrlAsync(int debugPort)
        {
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{debugPort}") };
            await WaitForDevToolsAsync(client);
            for (var i = 0; i < 50; i++)
            {
                using var response = await client.GetAsync("/json/list");
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                foreach (var target in document.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var type) && type.GetString() != "page")
                    {
                        continue;
                    }

                    if (target.TryGetProperty("webSocketDebuggerUrl", out var websocketUrl))
                    {
                        return websocketUrl.GetString()
                            ?? throw new InvalidOperationException("Browser DevTools target included an empty websocket URL.");
                    }
                }

                await Task.Delay(100);
            }

            throw new TimeoutException("Browser DevTools target list did not expose a page websocket URL.");
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
}
