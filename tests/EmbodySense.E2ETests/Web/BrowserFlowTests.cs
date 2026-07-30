using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed class BrowserFlowTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [InstalledBrowserFact]
    public async Task Default_chat_reconnects_after_process_restart_and_requires_a_fresh_authenticated_page()
    {
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsync(browser);
            await SubmitMessageAsync(browser, "browser-first-turn");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser response: browser-first-turn')");
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");

            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent.includes('reconnecting')");
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('sendButton').disabled"));

            app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/125): Require the first turn to be restored after the Web process restarts.
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");
            browser.ClearDiagnostics();
            await SubmitMessageAsync(browser, "browser-second-turn");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser response: browser-second-turn')");
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");

            var conversationEvidence = await ReadConversationEvidenceAsync(workspace);
            Assert.Contains("browser-first-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser response: browser-first-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser-second-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser response: browser-second-turn", conversationEvidence, StringComparison.Ordinal);
            app.AssertHealthy();
            browser.AssertHealthy();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Default_chat_reconnects_after_process_restart_and_requires_a_fresh_authenticated_page), browser, app);
            throw;
        }
        finally
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    [InstalledBrowserFact]
    public async Task Browser_authors_runs_inspects_and_deletes_a_governed_custom_loop()
    {
        using var workspace = new TestWorkspace();
        await File.WriteAllTextAsync(workspace.File("approval-note.txt"), "approved browser evidence");
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test");
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        const string LoopName = "Browser governed loop";

        try
        {
            await InitializeWorkspaceAsync(browser);
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop')");
            Assert.Equal(1, await browser.EvaluateInt32Async("[...document.querySelectorAll('#loopList .loop-list-item')].filter((item) => item.textContent.includes('System loop')).length"));
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('invokeButton').disabled && document.getElementById('saveButton').disabled && document.getElementById('deleteButton').disabled"));
            Assert.Contains("System definition is valid and read-only", await browser.EvaluateStringAsync("document.getElementById('validationBanner').textContent"), StringComparison.Ordinal);
            Assert.Contains("default-assistant", await browser.EvaluateStringAsync("document.getElementById('canvasAuthority').textContent"), StringComparison.Ordinal);

            await ClickAsync(browser, "#createLoopButton");
            await browser.WaitForExpressionAsync("!document.getElementById('loopName').disabled && document.querySelector('#loopCanvas .node-card.inference')");
            await SetValueAsync(browser, "#loopDescription", "Description survives validation correction and reload.");
            await SetValueAsync(browser, "#loopName", "");
            await browser.WaitForExpressionAsync("document.getElementById('validationBanner').textContent.includes('Loop name is required')");
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));
            await SetValueAsync(browser, "#loopName", LoopName);
            await ClickAsync(browser, "#loopCanvas .node-card.inference");
            await SetValueAsync(browser, "#inspectorContent input:not([type='checkbox'])", "Browser step");
            await SetValueAsync(browser, "#inspectorContent textarea", "Return deterministic browser evidence for the invocation prompt.");
            await SetValueAsync(browser, "#inspectorContent select", "custom", "change");
            await ClickAsync(browser, "#loopSettingsButton");
            await browser.EvaluateAsync("(() => { const row = [...document.querySelectorAll('#inspectorContent .checkbox-row')].find((item) => item.textContent.trim().startsWith('Read')); if (!row) throw new Error('Read assignment was not rendered.'); row.querySelector('input').click(); })()");
            await browser.WaitForExpressionAsync("!document.getElementById('saveButton').disabled && document.getElementById('validationBanner').textContent.includes('ready to save')");
            await ClickAsync(browser, "#saveButton");
            await browser.WaitForExpressionAsync("document.getElementById('saveState').textContent.includes('Saved') && document.getElementById('loopHeaderMeta').textContent.includes('Definition v2')");

            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("[...document.querySelectorAll('#loopList .loop-list-item')].some((item) => item.textContent.includes('Browser governed loop'))");
            browser.ClearDiagnostics();
            await ClickLoopByNameAsync(browser, LoopName);
            Assert.Equal(LoopName, await browser.EvaluateStringAsync("document.getElementById('loopName').value"));
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));

            await InvokeLoopAsync(browser, "browser-approval-approve");
            await browser.WaitForExpressionAsync("!document.getElementById('loopApprovalPanel').hidden && [...document.querySelectorAll('#loopApprovals button')].some((button) => button.textContent.includes('Approve'))");
            await ClickButtonByTextAsync(browser, "#loopApprovals button", "Approve");
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '1' && document.getElementById('runSubtitle').textContent.includes('· Completed') && document.getElementById('runTimeline').textContent.includes('browser governed tool response')");
            Assert.Contains("browser governed tool response", await browser.EvaluateStringAsync("document.getElementById('runTimeline').textContent"), StringComparison.OrdinalIgnoreCase);

            await ClickAsync(browser, "#builderTab");
            await InvokeLoopAsync(browser, "browser-approval-reject");
            await browser.WaitForExpressionAsync("!document.getElementById('loopApprovalPanel').hidden && [...document.querySelectorAll('#loopApprovals button')].some((button) => button.textContent.includes('Reject'))");
            await ClickButtonByTextAsync(browser, "#loopApprovals button", "Reject");
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '2' && document.getElementById('runSubtitle').textContent.includes('· Completed') && document.getElementById('runTimeline').textContent.toLowerCase().includes('rejected')");

            await ClickAsync(browser, "#builderTab");
            await InvokeLoopAsync(browser, "browser-provider-failure");
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '3' && document.getElementById('runSubtitle').textContent.includes('· Failed') && document.getElementById('runTimeline').textContent.includes('Provider attempt failed without an automatic retry')");
            Assert.False(await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#runActions button')].some((button) => /resume|cancel/i.test(button.textContent))"));

            await ClickAsync(browser, "#builderTab");
            await browser.EvaluateAsync("window.confirm = () => true");
            await ClickAsync(browser, "#deleteButton");
            await browser.WaitForExpressionAsync("document.getElementById('loopName').disabled && ![...document.querySelectorAll('#loopList .loop-list-item')].some((item) => item.textContent.includes('Browser governed loop'))");
            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("document.getElementById('loopList').textContent.includes('System loop')");
            Assert.False(await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#loopList .loop-list-item')].some((item) => item.textContent.includes('Browser governed loop'))"));
            app.AssertHealthy();
            browser.AssertHealthy();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_runs_inspects_and_deletes_a_governed_custom_loop), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Incompatible_runtime_is_visible_and_restores_chat_controls_after_rejection()
    {
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "older-model");
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test");
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsync(browser);
            await ClickAsync(browser, "#overviewNav");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('model-unavailable')");
            Assert.Contains("No discovered Codex executable advertises model", await browser.EvaluateStringAsync("document.getElementById('configContent').textContent"), StringComparison.Ordinal);
            await ClickAsync(browser, "#chatNav");
            await SubmitMessageAsync(browser, "browser-incompatible-runtime");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('Codex runtime is not usable')");
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");
            app.AssertHealthy();
            browser.AssertHealthy();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Incompatible_runtime_is_visible_and_restores_chat_controls_after_rejection), browser, app);
            throw;
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task InitializeWorkspaceAsync(HeadlessBrowserSession browser)
    {
        await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Needs initialization')");
        await browser.WaitForExpressionAsync("!document.getElementById('initButton').disabled");
        await ClickAsync(browser, "#initButton");
        await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
        await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
    }

    private static async Task SubmitMessageAsync(HeadlessBrowserSession browser, string message)
    {
        var jsonMessage = JsonSerializer.Serialize(message);
        await browser.EvaluateAsync("(() => { const input = document.getElementById('messageInput'); input.value = " + jsonMessage + "; document.getElementById('messageForm').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); })()");
    }

    private static async Task InvokeLoopAsync(HeadlessBrowserSession browser, string prompt)
    {
        await browser.WaitForExpressionAsync("!document.getElementById('invokeButton').disabled && !document.getElementById('startRunButton').disabled");
        await ClickAsync(browser, "#invokeButton");
        await browser.WaitForExpressionAsync("document.getElementById('invokeModal').classList.contains('open')");
        await SetValueAsync(browser, "#invocationPrompt", prompt);
        await ClickAsync(browser, "#startRunButton");
    }

    private static async Task ClickLoopByNameAsync(HeadlessBrowserSession browser, string name)
    {
        var jsonName = JsonSerializer.Serialize(name);
        await browser.EvaluateAsync("(() => { const item = [...document.querySelectorAll('#loopList .loop-list-item')].find((candidate) => candidate.textContent.includes(" + jsonName + ")); if (!item) throw new Error('Loop was not rendered: ' + " + jsonName + "); item.click(); })()");
        await browser.WaitForExpressionAsync("document.getElementById('loopName').value === " + jsonName);
    }

    private static async Task ClickButtonByTextAsync(HeadlessBrowserSession browser, string selector, string text)
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        var jsonText = JsonSerializer.Serialize(text);
        await browser.EvaluateAsync("(() => { const button = [...document.querySelectorAll(" + jsonSelector + ")].find((candidate) => candidate.textContent.includes(" + jsonText + ")); if (!button) throw new Error('Button was not rendered: ' + " + jsonText + "); button.click(); })()");
    }

    private static async Task ClickAsync(HeadlessBrowserSession browser, string selector)
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        await browser.EvaluateAsync("(() => { const element = document.querySelector(" + jsonSelector + "); if (!element) throw new Error('Element was not rendered: ' + " + jsonSelector + "); element.click(); })()");
    }

    private static async Task SetValueAsync(HeadlessBrowserSession browser, string selector, string value, string eventName = "input")
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        var jsonValue = JsonSerializer.Serialize(value);
        var jsonEventName = JsonSerializer.Serialize(eventName);
        await browser.EvaluateAsync("(() => { const element = document.querySelector(" + jsonSelector + "); if (!element) throw new Error('Element was not rendered: ' + " + jsonSelector + "); element.value = " + jsonValue + "; element.dispatchEvent(new Event(" + jsonEventName + ", { bubbles: true })); })()");
    }

    private static async Task<string> ReadConversationEvidenceAsync(TestWorkspace workspace)
    {
        var directory = workspace.File(".agent", "memory", "conversations");
        var files = Directory.EnumerateFiles(directory, "*.ndjson", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var contents = await Task.WhenAll(files.Select(path => File.ReadAllTextAsync(path)));
        return string.Join(Environment.NewLine, contents);
    }

    private static async Task WriteFailureDiagnosticsAsync(string scenario, HeadlessBrowserSession browser, ExternalWebApplicationProcess? app)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("EMBODYSENSE_BROWSER_E2E_ARTIFACTS");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(Path.Combine("tests", "EmbodySense.E2ETests", "TestResults", "BrowserE2E"))
            : Path.GetFullPath(configuredRoot);
        var directory = Path.Combine(root, scenario);
        Directory.CreateDirectory(directory);
        await browser.WriteDiagnosticsAsync(directory);
        if (app is not null)
        {
            await app.WriteDiagnosticsAsync(directory);
        }
    }

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
        private readonly List<string> _diagnostics = [];
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
                await session.SendCommandAsync("Log.enable");
                await session.SendCommandAsync("Network.enable");
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
            var startedAt = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(startedAt) < TimeSpan.FromSeconds(30))
            {
                try
                {
                    var value = await EvaluateAsync($"Boolean({expression})", CancellationToken.None);
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

        public async Task<bool> EvaluateBooleanAsync(string expression)
        {
            var value = await EvaluateAsync(expression, CancellationToken.None);
            return value.ValueKind == JsonValueKind.True;
        }

        public async Task<int> EvaluateInt32Async(string expression)
        {
            var value = await EvaluateAsync(expression, CancellationToken.None);
            return value.GetInt32();
        }

        public async Task ReloadAsync()
        {
            _ = await SendCommandAsync("Page.reload", new { ignoreCache = true });
        }

        public void ClearDiagnostics()
        {
            _diagnostics.Clear();
        }

        public void AssertHealthy()
        {
            Assert.False(_process.HasExited, $"Browser process exited unexpectedly.{Environment.NewLine}{FormatOutput()}");
            Assert.Empty(_diagnostics);
        }

        public async Task WriteDiagnosticsAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "browser-process.txt"), FormatOutput());
            await File.WriteAllLinesAsync(Path.Combine(directory, "browser-events.txt"), _diagnostics);
            try
            {
                var screenshot = await SendCommandAsync("Page.captureScreenshot", new { format = "png", captureBeyondViewport = true });
                var base64 = screenshot.GetProperty("result").GetProperty("data").GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    await File.WriteAllBytesAsync(Path.Combine(directory, "page.png"), Convert.FromBase64String(base64));
                }

                var html = await EvaluateStringAsync("document.documentElement.outerHTML");
                await File.WriteAllTextAsync(Path.Combine(directory, "page.html"), html);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "capture-error.txt"), exception.ToString());
            }
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
                ? JsonSerializer.Serialize(new { id = commandId, method }, _jsonOptions)
                : JsonSerializer.Serialize(new { id = commandId, method, @params = parameters }, _jsonOptions);
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
                RecordDiagnosticEvent(root);
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

        private void RecordDiagnosticEvent(JsonElement message)
        {
            if (!message.TryGetProperty("method", out var methodValue) || methodValue.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var method = methodValue.GetString();
            if (!message.TryGetProperty("params", out var parameters))
            {
                return;
            }

            if (method == "Runtime.exceptionThrown")
            {
                _diagnostics.Add("page exception: " + parameters.GetRawText());
                return;
            }

            if (method == "Runtime.consoleAPICalled"
                && parameters.TryGetProperty("type", out var consoleType)
                && string.Equals(consoleType.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.Add("console error: " + parameters.GetRawText());
                return;
            }

            if (method == "Log.entryAdded"
                && parameters.TryGetProperty("entry", out var entry)
                && entry.TryGetProperty("level", out var level)
                && string.Equals(level.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                if (IsKnownBaselineBrowserLogEntry(entry))
                {
                    return;
                }

                _diagnostics.Add("browser log error: " + entry.GetRawText());
                return;
            }

            if (method == "Network.responseReceived"
                && parameters.TryGetProperty("response", out var response)
                && response.TryGetProperty("status", out var status)
                && status.TryGetDouble(out var statusCode)
                && statusCode >= 500)
            {
                _diagnostics.Add("critical HTTP response: " + response.GetRawText());
                return;
            }

            if (method == "Network.loadingFailed"
                && (!parameters.TryGetProperty("canceled", out var cancelled) || cancelled.ValueKind != JsonValueKind.True))
            {
                _diagnostics.Add("network load failed: " + parameters.GetRawText());
            }
        }

        private static bool IsKnownBaselineBrowserLogEntry(JsonElement entry)
        {
            // TODO(https://github.com/Jacob-J-Thomas/agenthome-poc/issues/126): Remove these exceptions after the CSP and favicon load cleanly.
            var source = entry.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() : null;
            var text = entry.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
            var url = entry.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            return (string.Equals(source, "security", StringComparison.Ordinal)
                    && text?.Contains("ws://[::1]:*", StringComparison.Ordinal) == true
                || (string.Equals(source, "network", StringComparison.Ordinal)
                    && url?.EndsWith("/favicon.ico", StringComparison.Ordinal) == true
                    && text?.Contains("404", StringComparison.Ordinal) == true));
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
