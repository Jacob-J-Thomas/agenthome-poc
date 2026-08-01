using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Workspace;
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
        HeadlessBrowserSession? browser = null;

        try
        {
            browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
            await InitializeWorkspaceAsync(browser);
            await SubmitMessageAsync(browser, "browser-first-turn");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser response: browser-first-turn')");
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");
            await AssertChatRequestRegistryEmptyAsync(browser);

            app.AssertHealthy();
            browser.BeginExpectedServerRestart();
            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent.includes('reconnecting')");
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('sendButton').disabled"));
            await Task.Delay(TimeSpan.FromMilliseconds(1250));

            app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            browser.EndExpectedServerRestart();
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser-first-turn') && document.getElementById('transcript').textContent.includes('browser response: browser-first-turn')");
            Assert.Equal(1, await browser.EvaluateInt32Async("Array.from(document.querySelectorAll('#transcript .message.user')).filter(message => message.textContent.includes('browser-first-turn')).length"));
            Assert.Equal(1, await browser.EvaluateInt32Async("Array.from(document.querySelectorAll('#transcript .message.agent')).filter(message => message.textContent.includes('browser response: browser-first-turn')).length"));
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");
            await SubmitMessageAsync(browser, "browser-second-turn");
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser response: browser-second-turn')");
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");
            await AssertChatRequestRegistryEmptyAsync(browser);

            var conversationEvidence = await ReadConversationEvidenceAsync(workspace);
            Assert.Contains("browser-first-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser response: browser-first-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser-second-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser response: browser-second-turn", conversationEvidence, StringComparison.Ordinal);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Default_chat_reconnects_after_process_restart_and_requires_a_fresh_authenticated_page), browser, app);
            throw;
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }

            if (app is not null)
            {
                await app.DisposeAsync();
            }
        }
    }

    [InstalledBrowserFact]
    public async Task First_chat_turn_overlaps_configuration_refresh_without_sharing_violation_or_transcript_loss()
    {
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var currentTranscriptPath = workspace.File(".agent", "memory", "conversations", "current.ndjson");
        await File.WriteAllTextAsync(currentTranscriptPath, """{"schemaVersion":1,"conversationId":"current","sequence":1,"timestampUtc":"2026-07-30T00:00:00+00:00","messageId":"message-1","publicationId":"publication-1","role":"user","content":"configuration overlap seed"}""" + Environment.NewLine);
        await using var externalLease = new FileStream(currentTranscriptPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test");
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('refreshConfigButton').disabled");
            await SubmitMessageAsync(browser, "configuration-overlap-turn");
            await externalLease.DisposeAsync();
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser response: configuration-overlap-turn')");
            await browser.WaitForExpressionAsync("!document.getElementById('refreshConfigButton').disabled && !document.getElementById('sendButton').disabled");
            await ClickAsync(browser, "#historyNav");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('configuration overlap seed')");

            var configurationText = await browser.EvaluateStringAsync("document.getElementById('configContent').textContent");
            var conversationEvidence = await ReadConversationEvidenceAsync(workspace);
            Assert.DoesNotContain("Configuration unavailable:", configurationText, StringComparison.Ordinal);
            Assert.Contains("configuration overlap seed", configurationText, StringComparison.Ordinal);
            Assert.Contains("configuration overlap seed", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("configuration-overlap-turn", conversationEvidence, StringComparison.Ordinal);
            Assert.Contains("browser response: configuration-overlap-turn", conversationEvidence, StringComparison.Ordinal);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(First_chat_turn_overlaps_configuration_refresh_without_sharing_violation_or_transcript_loss), browser, app);
            throw;
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
            Assert.Equal(5, await browser.EvaluateInt32Async("document.querySelectorAll('#loopCanvas .node-card').length"));
            Assert.Equal(4, await browser.EvaluateInt32Async("document.querySelectorAll('#loopCanvas .system-connector-label').length"));
            var systemCanvas = await browser.EvaluateStringAsync("document.getElementById('loopCanvas').textContent");
            Assert.Contains("Accept user message", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("Assemble runtime context", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("Dispatch provider inference", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("Persist transcript", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("Complete loop run", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("accept-message-to-context", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("transcript-to-complete-run", systemCanvas, StringComparison.Ordinal);
            Assert.DoesNotContain("Manual trigger", systemCanvas, StringComparison.Ordinal);
            Assert.DoesNotContain("Respond in role", systemCanvas, StringComparison.Ordinal);
            Assert.Contains("5 nodes · 4 edges", await browser.EvaluateStringAsync("document.getElementById('loopHeaderMeta').textContent"), StringComparison.Ordinal);
            Assert.Contains("not dispatched by the custom-loop or a generic graph executor", await browser.EvaluateStringAsync("document.getElementById('validationBanner').textContent"), StringComparison.Ordinal);
            await ClickAsync(browser, "#loopSettingsButton");
            var systemPolicy = await browser.EvaluateStringAsync("document.getElementById('inspectorContent').textContent");
            Assert.Contains("Human message", systemPolicy, StringComparison.Ordinal);
            Assert.Contains("Workspace startup context", systemPolicy, StringComparison.Ordinal);
            Assert.Contains("workspace.command", systemPolicy, StringComparison.Ordinal);
            Assert.Contains("Generic graph dispatch: Not implemented", systemPolicy, StringComparison.Ordinal);

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
            await ClickLoopByNameAsync(browser, LoopName);
            Assert.Equal(LoopName, await browser.EvaluateStringAsync("document.getElementById('loopName').value"));
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));

            await InvokeLoopAsync(browser, "browser-approval-approve");
            await browser.WaitForExpressionAsync("!document.getElementById('loopApprovalPanel').hidden && [...document.querySelectorAll('#loopApprovals button')].some((button) => button.textContent.includes('Approve'))");
            await ClickButtonByTextAsync(browser, "#loopApprovals button", "Approve");
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '1' && document.getElementById('runSubtitle').textContent.includes('· Completed') && document.getElementById('runTimeline').textContent.includes('approved browser evidence')");
            Assert.Contains("browser governed tool approved", await browser.EvaluateStringAsync("document.getElementById('runTimeline').textContent"), StringComparison.OrdinalIgnoreCase);

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
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_runs_inspects_and_deletes_a_governed_custom_loop), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Browser_lazily_inspects_and_explicitly_requests_bounded_receipt_cleanup()
    {
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test");
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsync(browser);
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop')");
            Assert.Equal(0, await browser.EvaluateInt32Async("performance.getEntriesByType('resource').filter((entry) => entry.name.endsWith('/api/loops/receipt-retention')).length"));
            Assert.Equal(0, await browser.EvaluateInt32Async("performance.getEntriesByType('resource').filter((entry) => entry.name.endsWith('/api/loops/receipt-retention/cleanup')).length"));

            await ClickAsync(browser, "#retentionTab");
            await browser.WaitForExpressionAsync("document.getElementById('retentionContent').textContent.includes('Definition Mutation Receipt') && document.getElementById('retentionContent').textContent.includes('Exact replay horizon')");
            Assert.Equal(1, await browser.EvaluateInt32Async("performance.getEntriesByType('resource').filter((entry) => entry.name.endsWith('/api/loops/receipt-retention')).length"));
            await Task.Delay(250);
            Assert.Equal(0, await browser.EvaluateInt32Async("performance.getEntriesByType('resource').filter((entry) => entry.name.endsWith('/api/loops/receipt-retention/cleanup')).length"));
            Assert.True(
                await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#retentionContent .retention-cleanup-button')].some((button) => !button.disabled)"),
                await browser.EvaluateStringAsync("document.getElementById('retentionContent').textContent"));

            var paths = new WorkspacePaths(workspace.RootPath);
            var ownershipAcquiredAtUtc = DateTimeOffset.UtcNow.AddSeconds(-25);
            var interruptedRequest = new CustomLoopReceiptCleanupRequest(
                CustomLoopReceiptCleanupRequest.CurrentSchemaVersion,
                CustomLoopReceiptArtifactClass.DefinitionMutationReceipt,
                "browser-retention-recovery",
                "embodysense.web",
                "web",
                ownershipAcquiredAtUtc,
                CustomLoopReceiptRetentionPolicy.GetReplayCutoffUtc(ownershipAcquiredAtUtc),
                64,
                4 * 1024 * 1024);
            var interruptedJournal = new CustomLoopReceiptCleanupJournal(
                CustomLoopReceiptCleanupJournal.CurrentSchemaVersion,
                interruptedRequest,
                CustomLoopReceiptRetentionContractCodec.ComputeCleanupRequestHash(interruptedRequest),
                "cleanup-owner-interrupted-browser",
                Environment.ProcessId,
                ownershipAcquiredAtUtc,
                CustomLoopReceiptCleanupStage.IntentPersisted,
                CustomLoopReceiptCleanupOutcome.Unknown,
                ownershipAcquiredAtUtc,
                ImmutableArray<CustomLoopReceiptCleanupCandidate>.Empty,
                null,
                0,
                0,
                "The browser recovery test interrupted cleanup after its durable intent.");
            Directory.CreateDirectory(paths.CustomLoopReceiptRetentionPath);
            await File.WriteAllBytesAsync(paths.CustomLoopDefinitionMutationReceiptCleanupJournalPath, CustomLoopReceiptRetentionContractCodec.SerializeCleanupJournal(interruptedJournal));

            await ClickAsync(browser, "#refreshRetentionButton");
            const string TargetCleanup = "[...document.querySelectorAll('#retentionContent .retention-class-card')].find((card) => card.textContent.includes('Definition Mutation Receipt')).querySelector('.retention-cleanup-button')";
            await browser.WaitForExpressionAsync("document.getElementById('retentionContent').textContent.toLowerCase().includes('recovery pending') && " + TargetCleanup + ".disabled");
            Assert.Contains("Recovery available", await browser.EvaluateStringAsync("document.getElementById('retentionContent').textContent"), StringComparison.OrdinalIgnoreCase);
            await browser.WaitForExpressionAsync("!" + TargetCleanup + ".disabled && " + TargetCleanup + ".textContent.includes('Retry cleanup recovery')");

            await browser.EvaluateAsync("window.__retentionConfirmation = ''; window.confirm = (message) => { window.__retentionConfirmation = message; return true; };");
            await browser.EvaluateAsync(TargetCleanup + ".click()");
            await browser.WaitForExpressionAsync("document.getElementById('retentionNotice').textContent.includes('Nothing Eligible')");

            Assert.Contains("64 artifacts", await browser.EvaluateStringAsync("window.__retentionConfirmation"), StringComparison.Ordinal);
            Assert.Contains("4 MiB", await browser.EvaluateStringAsync("window.__retentionConfirmation"), StringComparison.Ordinal);
            Assert.Equal(1, await browser.EvaluateInt32Async("performance.getEntriesByType('resource').filter((entry) => entry.name.endsWith('/api/loops/receipt-retention/cleanup')).length"));
            Assert.Equal(3, await browser.EvaluateInt32Async("performance.getEntriesByType('resource').filter((entry) => entry.name.endsWith('/api/loops/receipt-retention')).length"));
            Assert.Contains("No eligible expired receipt evidence was available for cleanup.", await browser.EvaluateStringAsync("document.getElementById('retentionNotice').textContent"), StringComparison.OrdinalIgnoreCase);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_lazily_inspects_and_explicitly_requests_bounded_receipt_cleanup), browser, app);
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
            await browser.AssertHealthyAsync();
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
        await browser.EvaluateAsync("(() => { const input = document.getElementById('messageInput'); const send = document.getElementById('sendButton'); const cancel = document.getElementById('cancelButton'); input.value = " + jsonMessage + "; document.getElementById('messageForm').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); if (input.value !== '' || !send.disabled || cancel.disabled) throw new Error('The browser did not synchronously accept the submitted turn.'); })()");
    }

    private static async Task AssertChatRequestRegistryEmptyAsync(HeadlessBrowserSession browser)
    {
        const string Expression = "(() => { const raw = localStorage.getItem('embodysense.chat-requests.v1'); if (!raw) return false; const registry = JSON.parse(raw); return Object.keys(registry).sort().join(',') === 'entries,schemaVersion,scope' && registry.schemaVersion === 1 && /^[0-9a-f]{64}$/.test(registry.scope) && Array.isArray(registry.entries) && registry.entries.length === 0 && !raw.includes('access_token'); })()";
        Assert.True(await browser.EvaluateBooleanAsync(Expression));
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
        var snapshot = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadConversationHistorySnapshotAsync(50, 400, 4_000_000);
        return string.Join(Environment.NewLine, snapshot.Transcripts.SelectMany(transcript => transcript.Lines));
    }

    private static async Task WriteFailureDiagnosticsAsync(string scenario, HeadlessBrowserSession? browser, ExternalWebApplicationProcess? app)
    {
        var configuredRoot = Environment.GetEnvironmentVariable("EMBODYSENSE_BROWSER_E2E_ARTIFACTS");
        var root = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.GetFullPath(Path.Combine("tests", "EmbodySense.E2ETests", "TestResults", "BrowserE2E"))
            : Path.GetFullPath(configuredRoot);
        var directory = Path.Combine(root, scenario);
        Directory.CreateDirectory(directory);
        if (browser is not null)
        {
            await browser.WriteDiagnosticsAsync(directory);
        }

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
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCommands = new();
        private readonly ConcurrentDictionary<int, Task> _pendingSends = new();
        private readonly ConcurrentDictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _diagnosticsGate = new();
        private readonly List<string> _diagnostics = [];
        private readonly byte[] _buffer = new byte[65536];
        private readonly Task _readerTask;
        private readonly string _targetAuthority;
        private Exception? _readerFailure;
        private int _expectedServerRestart;
        private int _nextCommandId;
        private int _disposed;

        private HeadlessBrowserSession(Process process, ClientWebSocket socket, string userDataDirectory, BoundedProcessOutput output, BoundedProcessOutput error, string targetUrl)
        {
            _process = process;
            _socket = socket;
            _userDataDirectory = userDataDirectory;
            _output = output;
            _error = error;
            _targetAuthority = new Uri(targetUrl).Authority;
            _readerTask = ReceiveLoopAsync();
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

            HeadlessBrowserSession? session = null;
            try
            {
                var websocketUrl = await GetInitialPageWebSocketUrlAsync(debugPort);
                var socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(websocketUrl), CancellationToken.None);
                session = new HeadlessBrowserSession(process, socket, userDataDirectory, output, error, targetUrl);
                await session.SendCommandAsync("Page.enable");
                await session.SendCommandAsync("Runtime.enable");
                await session.SendCommandAsync("Log.enable");
                await session.SendCommandAsync("Network.enable");
                await session.SendCommandAsync("Page.navigate", new { url = targetUrl });
                return session;
            }
            catch (Exception exception)
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
                else
                {
                    await StopProcessAsync(process);
                    TryDeleteDirectory(userDataDirectory);
                }

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
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or JsonException)
                {
                    lastException = exception;
                }

                try
                {
                    await Task.Delay(100, timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    break;
                }
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

        public void BeginExpectedServerRestart()
        {
            Interlocked.Exchange(ref _expectedServerRestart, 1);
        }

        public void EndExpectedServerRestart()
        {
            Interlocked.Exchange(ref _expectedServerRestart, 0);
        }

        public async Task AssertHealthyAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = await EvaluateAsync("true", timeout.Token);
            Assert.False(_process.HasExited, $"Browser process exited unexpectedly.{Environment.NewLine}{FormatOutput()}");
            Assert.Null(_readerFailure);
            Assert.Empty(GetDiagnosticsSnapshot());
        }

        public async Task WriteDiagnosticsAsync(string directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "browser-process.txt"), FormatOutput());
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var screenshot = await SendCommandAsync("Page.captureScreenshot", new { format = "png", captureBeyondViewport = true }, timeout.Token);
                var base64 = screenshot.GetProperty("result").GetProperty("data").GetString();
                if (!string.IsNullOrWhiteSpace(base64))
                {
                    await File.WriteAllBytesAsync(Path.Combine(directory, "page.png"), Convert.FromBase64String(base64));
                }

                var html = (await EvaluateAsync("document.documentElement.outerHTML", timeout.Token)).GetString() ?? "";
                await File.WriteAllTextAsync(Path.Combine(directory, "page.html"), html);
            }
            catch (Exception exception)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "capture-error.txt"), exception.ToString());
            }

            await File.WriteAllLinesAsync(Path.Combine(directory, "browser-events.txt"), GetDiagnosticsSnapshot());
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test complete", timeout.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or WebSocketException)
                {
                    _socket.Abort();
                }
            }

            _socket.Dispose();
            try
            {
                await _readerTask;
            }
            catch (Exception exception) when (exception is InvalidOperationException or WebSocketException or ObjectDisposedException)
            {
            }

            var pendingSends = _pendingSends.Values.ToArray();
            if (pendingSends.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pendingSends).WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception exception) when (exception is TimeoutException or WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
                {
                }
            }

            if (_pendingSends.IsEmpty)
            {
                _sendGate.Dispose();
            }

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
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfReaderFailed();
            var commandId = Interlocked.Increment(ref _nextCommandId);
            var payload = parameters is null
                ? JsonSerializer.Serialize(new { id = commandId, method }, _jsonOptions)
                : JsonSerializer.Serialize(new { id = commandId, method, @params = parameters }, _jsonOptions);
            var bytes = Encoding.UTF8.GetBytes(payload);
            var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingCommands.TryAdd(commandId, completion))
            {
                throw new InvalidOperationException($"Browser DevTools command id {commandId} was already pending.");
            }

            try
            {
                var sendTask = SendPayloadAsync(bytes);
                _pendingSends[commandId] = sendTask;
                _ = ObserveSendCompletionAsync(commandId, sendTask);
                await sendTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _pendingCommands.TryRemove(commandId, out _);
                throw;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                _pendingCommands.TryRemove(commandId, out _);
                throw new InvalidOperationException("Browser DevTools command send failed." + Environment.NewLine + FormatOutput(), exception);
            }

            try
            {
                return await completion.Task.WaitAsync(cancellationToken);
            }
            catch
            {
                _pendingCommands.TryRemove(commandId, out _);
                throw;
            }
        }

        private async Task SendPayloadAsync(byte[] bytes)
        {
            await _sendGate.WaitAsync(CancellationToken.None);
            try
            {
                ThrowIfReaderFailed();
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task ObserveSendCompletionAsync(int commandId, Task sendTask)
        {
            try
            {
                await sendTask;
            }
            catch
            {
            }
            finally
            {
                _pendingSends.TryRemove(commandId, out _);
            }
        }

        private async Task ReceiveLoopAsync()
        {
            Exception? failure = null;
            try
            {
                while (Volatile.Read(ref _disposed) == 0 && _socket.State == WebSocketState.Open)
                {
                    using var document = await ReadMessageAsync(CancellationToken.None);
                    var root = document.RootElement;
                    if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var commandId))
                    {
                        if (_pendingCommands.TryRemove(commandId, out var completion))
                        {
                            completion.TrySetResult(root.Clone());
                        }

                        continue;
                    }

                    RecordDiagnosticEvent(root);
                }
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                failure = exception;
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _readerFailure = exception;
                }
            }
            finally
            {
                var completionFailure = _readerFailure
                    ?? failure
                    ?? new ObjectDisposedException(nameof(HeadlessBrowserSession));
                foreach (var pending in _pendingCommands.ToArray())
                {
                    if (_pendingCommands.TryRemove(pending.Key, out var completion))
                    {
                        completion.TrySetException(completionFailure);
                    }
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

            CaptureRequestUrl(method, parameters);

            if (method == "Runtime.exceptionThrown")
            {
                AddDiagnostic("page exception: " + parameters.GetRawText());
                return;
            }

            if (method == "Runtime.consoleAPICalled"
                && parameters.TryGetProperty("type", out var consoleType)
                && string.Equals(consoleType.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                AddDiagnostic("console error: " + parameters.GetRawText());
                return;
            }

            if (method == "Log.entryAdded"
                && parameters.TryGetProperty("entry", out var entry)
                && entry.TryGetProperty("level", out var level)
                && string.Equals(level.GetString(), "error", StringComparison.OrdinalIgnoreCase))
            {
                if (IsExpectedServerRestartLogEntry(entry))
                {
                    return;
                }

                AddDiagnostic("browser log error: " + entry.GetRawText());
                return;
            }

            if (method == "Network.responseReceived"
                && parameters.TryGetProperty("response", out var response)
                && response.TryGetProperty("status", out var status)
                && status.TryGetDouble(out var statusCode)
                && statusCode >= 400)
            {
                AddDiagnostic("HTTP error response: " + response.GetRawText());
                return;
            }

            if (method == "Network.loadingFailed"
                && (!parameters.TryGetProperty("canceled", out var cancelled) || cancelled.ValueKind != JsonValueKind.True))
            {
                if (IsExpectedServerRestartNetworkFailure(parameters))
                {
                    return;
                }

                AddDiagnostic("network load failed: " + parameters.GetRawText());
            }
        }

        private void CaptureRequestUrl(string? method, JsonElement parameters)
        {
            if (!parameters.TryGetProperty("requestId", out var requestIdValue) || requestIdValue.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var requestId = requestIdValue.GetString()!;
            if (method == "Network.requestWillBeSent"
                && parameters.TryGetProperty("request", out var request)
                && request.TryGetProperty("url", out var requestUrl)
                && requestUrl.ValueKind == JsonValueKind.String)
            {
                _requestUrls[requestId] = requestUrl.GetString()!;
                return;
            }

            if (method == "Network.webSocketCreated"
                && parameters.TryGetProperty("url", out var websocketUrl)
                && websocketUrl.ValueKind == JsonValueKind.String)
            {
                _requestUrls[requestId] = websocketUrl.GetString()!;
                return;
            }

            if (method is "Network.loadingFinished" or "Network.webSocketClosed")
            {
                _requestUrls.TryRemove(requestId, out _);
            }
        }

        private bool IsExpectedServerRestartLogEntry(JsonElement entry)
        {
            if (Volatile.Read(ref _expectedServerRestart) == 0)
            {
                return false;
            }

            var source = entry.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() : null;
            var text = entry.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
            var url = entry.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            return string.Equals(source, "network", StringComparison.Ordinal)
                && (ContainsTargetAuthority(text) || ContainsTargetAuthority(url))
                && (text?.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) == true || url?.StartsWith("ws", StringComparison.OrdinalIgnoreCase) == true)
                && (text?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true || text?.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase) == true);
        }

        private bool IsExpectedServerRestartNetworkFailure(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("requestId", out var requestIdValue)
                || requestIdValue.ValueKind != JsonValueKind.String
                || !_requestUrls.TryRemove(requestIdValue.GetString()!, out var requestUrl))
            {
                return false;
            }

            if (Volatile.Read(ref _expectedServerRestart) == 0
                || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Authority, _targetAuthority, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase) && !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var errorText = parameters.TryGetProperty("errorText", out var errorTextValue) ? errorTextValue.GetString() : null;
            return errorText?.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase) == true
                || errorText?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true;
        }

        private bool ContainsTargetAuthority(string? value)
        {
            return value?.Contains(_targetAuthority, StringComparison.OrdinalIgnoreCase) == true;
        }

        private void AddDiagnostic(string diagnostic)
        {
            lock (_diagnosticsGate)
            {
                _diagnostics.Add(diagnostic);
            }
        }

        private IReadOnlyList<string> GetDiagnosticsSnapshot()
        {
            lock (_diagnosticsGate)
            {
                return _diagnostics.ToArray();
            }
        }

        private void ThrowIfReaderFailed()
        {
            if (_readerFailure is not null)
            {
                throw new InvalidOperationException("Browser DevTools reader failed." + Environment.NewLine + FormatOutput(), _readerFailure);
            }
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
