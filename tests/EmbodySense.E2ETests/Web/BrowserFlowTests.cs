using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EmbodySense.E2ETests.Web;

public sealed class BrowserFlowTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    [InstalledBrowserFact]
    public async Task Default_chat_recovers_in_place_after_process_restart_and_preserves_unsaved_draft()
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
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('createLoopButton').disabled && document.getElementById('saveState').textContent === 'System managed'");
            await ClickAsync(browser, "#createLoopButton");
            await browser.WaitForExpressionAsync("!document.getElementById('loopDescription').disabled");
            await SetValueAsync(browser, "#loopDescription", "unsaved restart draft");
            var draftStored = await browser.EvaluateBooleanAsync("Array.from({ length: sessionStorage.length }, (_, index) => sessionStorage.getItem(sessionStorage.key(index))).some(value => value && value.includes('unsaved restart draft'))");
            var saveState = await browser.EvaluateStringAsync("document.getElementById('saveState').textContent");
            var validationState = await browser.EvaluateStringAsync("document.getElementById('validationBanner').textContent");
            Assert.True(draftStored, $"The unsaved draft was not stored. Save state: {saveState}. Validation: {validationState}");
            await ClickAsync(browser, "#chatNav");

            app.AssertHealthy();
            browser.BeginExpectedServerRestart();
            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('sendButton').disabled"));
            await Task.Delay(TimeSpan.FromMilliseconds(1250));

            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent === 'Web primary'");
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            Assert.True(await browser.EvaluateBooleanAsync("Array.from({ length: sessionStorage.length }, (_, index) => sessionStorage.getItem(sessionStorage.key(index))).some(value => value && value.includes('unsaved restart draft'))"), "The unsaved draft storage was cleared during host recovery.");
            browser.EndExpectedServerRestart();
            await browser.WaitForExpressionAsync("document.getElementById('transcript').textContent.includes('browser-first-turn') && document.getElementById('transcript').textContent.includes('browser response: browser-first-turn')");
            Assert.Equal(1, await browser.EvaluateInt32Async("Array.from(document.querySelectorAll('#transcript .message.user')).filter(message => message.textContent.includes('browser-first-turn')).length"));
            Assert.Equal(1, await browser.EvaluateInt32Async("Array.from(document.querySelectorAll('#transcript .message.agent')).filter(message => message.textContent.includes('browser response: browser-first-turn')).length"));
            await browser.WaitForExpressionAsync("!document.getElementById('sendButton').disabled && document.getElementById('cancelButton').disabled");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("document.getElementById('loopDescription').value === 'unsaved restart draft'");
            Assert.Equal("unsaved restart draft", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));
            Assert.False(await browser.EvaluateBooleanAsync("document.getElementById('saveButton').disabled"));
            await ClickAsync(browser, "#chatNav");
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
            await WriteFailureDiagnosticsAsync(nameof(Default_chat_recovers_in_place_after_process_restart_and_preserves_unsaved_draft), browser, app);
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
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
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
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
        HeadlessBrowserSession? browser = null;
        string? retiredServerOutput = null;
        const string LoopName = "Browser governed loop";

        try
        {
            browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
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
            Assert.Contains("does not certify the nodes and edges as an exact execution-order contract", await browser.EvaluateStringAsync("document.getElementById('validationBanner').textContent"), StringComparison.Ordinal);
            await ClickAsync(browser, "#loopSettingsButton");
            var systemPolicy = await browser.EvaluateStringAsync("document.getElementById('inspectorContent').textContent");
            Assert.Contains("Human message", systemPolicy, StringComparison.Ordinal);
            Assert.Contains("Workspace startup context", systemPolicy, StringComparison.Ordinal);
            Assert.Contains("workspace.command", systemPolicy, StringComparison.Ordinal);
            Assert.Contains("Generic graph dispatch: Not implemented", systemPolicy, StringComparison.Ordinal);

            await ClickAsync(browser, "#createLoopButton");
            await browser.WaitForExpressionAsync("!document.getElementById('loopName').disabled && document.querySelector('#loopCanvas .node-card.inference')");
            Assert.Contains("Unsaved draft", await browser.EvaluateStringAsync("document.getElementById('saveState').textContent"), StringComparison.Ordinal);
            Assert.Contains("Not durable", await browser.EvaluateStringAsync("document.getElementById('loopList').textContent"), StringComparison.Ordinal);
            Assert.Equal(0, await GetCustomDefinitionCountAsync(browser));
            await browser.EvaluateAsync("window.confirm = () => true");
            await ClickAsync(browser, "#reloadButton");
            await browser.WaitForExpressionAsync("document.getElementById('saveState').textContent.includes('System managed')");
            Assert.False(await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#loopList .loop-list-item')].some((item) => item.textContent.includes('Untitled loop'))"));
            Assert.Equal(0, await GetCustomDefinitionCountAsync(browser));

            await ClickAsync(browser, "#createLoopButton");
            await browser.WaitForExpressionAsync("!document.getElementById('loopName').disabled && document.querySelector('#loopCanvas .node-card.inference')");
            await SetValueAsync(browser, "#loopDescription", "Description survives validation correction and reload.");
            await SetValueAsync(browser, "#loopName", "");
            await browser.WaitForExpressionAsync("document.getElementById('validationBanner').textContent.includes('Loop name is required')");
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));
            await SetValueAsync(browser, "#loopName", LoopName);
            await browser.ReloadAsync(acceptBeforeUnload: true);
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("document.getElementById('loopName').value === 'Browser governed loop' && document.getElementById('saveState').textContent.includes('Unsaved draft')");
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));
            Assert.Equal(0, await GetCustomDefinitionCountAsync(browser));

            browser.BeginExpectedServerRestart();
            await app.DisposeAsync();
            retiredServerOutput = app.FormatOutput();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
            await browser.ReloadAsync(acceptBeforeUnload: true);
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
            browser.EndExpectedServerRestart();
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("document.getElementById('loopName').value === 'Browser governed loop' && document.getElementById('saveState').textContent.includes('Unsaved draft')");
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));
            Assert.Equal(0, await GetCustomDefinitionCountAsync(browser));

            await ClickAsync(browser, "#loopCanvas .node-card.inference");
            await SetValueAsync(browser, "#inspectorContent input:not([type='checkbox'])", "Browser step");
            await SetValueAsync(browser, "#inspectorContent textarea", "Return deterministic browser evidence for the invocation prompt.");
            await SetValueAsync(browser, "#inspectorContent select", "custom", "change");
            await ClickAsync(browser, "#loopSettingsButton");
            await browser.EvaluateAsync("(() => { const row = [...document.querySelectorAll('#inspectorContent .checkbox-row')].find((item) => item.textContent.trim().startsWith('Read')); if (!row) throw new Error('Read assignment was not rendered.'); row.querySelector('input').click(); })()");
            await browser.WaitForExpressionAsync("!document.getElementById('saveButton').disabled && document.getElementById('validationBanner').textContent.includes('ready for first save')");
            await ClickAsync(browser, "#saveButton");
            await browser.WaitForExpressionAsync("document.getElementById('saveState').textContent.includes('Saved') && document.getElementById('loopHeaderMeta').textContent.includes('Definition v1')");
            Assert.Equal(1, await GetCustomDefinitionCountAsync(browser));

            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("[...document.querySelectorAll('#loopList .loop-list-item')].some((item) => item.textContent.includes('Browser governed loop'))");
            await ClickLoopByNameAsync(browser, LoopName);
            Assert.Equal(LoopName, await browser.EvaluateStringAsync("document.getElementById('loopName').value"));
            Assert.Equal("Description survives validation correction and reload.", await browser.EvaluateStringAsync("document.getElementById('loopDescription').value"));
            Assert.Equal(1, await GetCustomDefinitionCountAsync(browser));

            await InvokeLoopAsync(browser, "browser-approval-approve");
            await browser.WaitForExpressionAsync("!document.getElementById('loopApprovalPanel').hidden && [...document.querySelectorAll('#loopApprovals button')].some((button) => button.textContent.includes('Approve'))");
            await ClickButtonByTextAsync(browser, "#loopApprovals button", "Approve");
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '1' && document.getElementById('runSubtitle').textContent.includes('· Completed') && document.getElementById('runTimeline').textContent.includes('approved browser evidence')");
            Assert.Contains("browser governed tool approved", await browser.EvaluateStringAsync("document.getElementById('runTimeline').textContent"), StringComparison.OrdinalIgnoreCase);
            var publicationInspector = await browser.EvaluateStringAsync("document.getElementById('inspectorContent').textContent");
            Assert.Contains("Published", publicationInspector, StringComparison.Ordinal);
            Assert.Contains("definite", publicationInspector, StringComparison.Ordinal);
            Assert.DoesNotContain("not published", publicationInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("terminal outcome recorded", await browser.EvaluateStringAsync("document.getElementById('runTimeline').textContent"), StringComparison.Ordinal);

            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("[...document.querySelectorAll('#loopList .loop-list-item')].some((item) => item.textContent.includes('Browser governed loop'))");
            await ClickLoopByNameAsync(browser, LoopName);
            await ClickAsync(browser, "#runsTab");
            await browser.WaitForExpressionAsync("document.getElementById('inspectorContent').textContent.includes('Published') && !document.getElementById('inspectorContent').textContent.toLowerCase().includes('not published')");

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
            Assert.Equal(0, await GetCustomDefinitionCountAsync(browser));
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_runs_inspects_and_deletes_a_governed_custom_loop), browser, app, retiredServerOutput);
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
    public async Task Browser_authors_publishes_and_reloads_a_server_cataloged_schedule_graph()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var serverAccountHome = serverAccount.RootPath;
        var localApplicationData = OperatingSystem.IsMacOS()
            ? Path.Combine(serverAccountHome, "Library", "Application Support")
            : Path.Combine(serverAccountHome, "local-data");
        var capabilityTrustRoot = Path.Combine(localApplicationData, "EmbodySense", "server-state", "capability-catalog");
        Directory.CreateDirectory(localApplicationData);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var authoringRole = await CreateScheduleGraphAuthoringRoleAsync(paths);
        var serverEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CFFIXED_USER_HOME"] = serverAccountHome,
            ["EMBODYSENSE_CAPABILITY_CATALOG_TRUST_ROOT"] = capabilityTrustRoot,
            ["LOCALAPPDATA"] = localApplicationData,
            ["XDG_DATA_HOME"] = localApplicationData,
        };
        await using var app = await ExternalWebApplicationProcess.StartAsync(
            workspace.RootPath,
            GetFreePort(),
            codexExecutable,
            "gpt-test",
            serverEnvironment);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop')");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphCatalog').textContent.includes('schedule-trigger') && document.getElementById('governedGraphCatalog').textContent.includes('provider-inference') && document.getElementById('governedGraphCatalog').textContent.includes('success-exit')");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphModelProfile').value === 'org.embodysense/model-profile/codex' && document.getElementById('governedGraphModelProfile').textContent.includes('gpt-test') && document.getElementById('governedGraphModelProfile').textContent.includes('configured default')");

            await SetValueAsync(
                browser,
                "#governedGraphRole",
                $"{authoringRole.Identity.RoleId}:{authoringRole.Identity.Revision}:{authoringRole.ContentHash}",
                "change");
            await SetValueAsync(browser, "#governedGraphModelRoutingMode", "inherit", "change");
            await SetValueAsync(browser, "#governedGraphId", "browser-scheduled-graph");
            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-1");
            await SetValueAsync(browser, "#governedGraphDisplayName", "Browser scheduled graph");
            await SetValueAsync(browser, "#governedGraphPurpose", "Publish one server-cataloged scheduled graph.");
            await ClickAsync(browser, "#governedGraphNewButton");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "schedule-trigger");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "provider-inference");
            await SetValueAsync(browser, "#governedGraphInspector input:not([type='number'])", "Return the exact scheduled request.");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Model profile evidence') && !document.getElementById('governedGraphInspector').textContent.includes('Loading')");
            var modelInspector = await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent");
            Assert.Contains("Eligible", modelInspector, StringComparison.Ordinal);
            Assert.Contains("runtime admission still required", modelInspector, StringComparison.Ordinal);
            Assert.Contains("org.embodysense/model-profile/codex", modelInspector, StringComparison.Ordinal);
            Assert.Contains("sensitive", modelInspector, StringComparison.Ordinal);
            Assert.Contains("remote", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("attempt input unbounded", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("node input unbounded", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("run input unbounded", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Input Tokens Authoritative after dispatch", modelInspector, StringComparison.Ordinal);
            Assert.Contains("Monetary Cost Unavailable", modelInspector, StringComparison.Ordinal);
            Assert.Contains("Ordered model fallback candidatesNone", modelInspector, StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "success-exit");

            await AddGovernedGraphControlAsync(browser, "schedule-trigger", "provider-inference", "Always");
            await AddGovernedGraphBindingAsync(browser, "schedule-trigger", "provider-inference", "Data · request → request");
            await AddGovernedGraphBindingAsync(browser, "schedule-trigger", "provider-inference", "Context · invocation-context → invocation-context");
            await AddGovernedGraphControlAsync(browser, "provider-inference", "success-exit", "Success");
            await AddGovernedGraphBindingAsync(browser, "provider-inference", "success-exit", "Data · result → result");

            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphSaveButton').disabled");
            await ClickAsync(browser, "#governedGraphSaveButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Draft') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphPublishButton').disabled");
            await ClickAsync(browser, "#governedGraphPublishButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            Assert.Contains("schedule-trigger", await browser.EvaluateStringAsync("document.getElementById('governedGraphCanvas').textContent"), StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "schedule-trigger");
            Assert.Contains("org.embodysense/triggers/time", await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent"), StringComparison.Ordinal);

            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.querySelectorAll('#governedGraphCanvas .governed-graph-node').length === 3");
            Assert.Equal("browser-scheduled-graph", await browser.EvaluateStringAsync("document.getElementById('governedGraphId').value"));
            Assert.Equal("org.embodysense/model-profile/codex", await browser.EvaluateStringAsync("document.getElementById('governedGraphModelProfile').value"));
            Assert.Equal("inherit", await browser.EvaluateStringAsync("document.getElementById('governedGraphModelRoutingMode').value"));
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "provider-inference");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Eligible') && document.getElementById('governedGraphInspector').textContent.includes('inherit selector')");
            Assert.Contains("org.embodysense/model-profile/codex", await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent"), StringComparison.Ordinal);
            Assert.Contains("1 immutable revision artifact", await browser.EvaluateStringAsync("document.getElementById('governedGraphLifecycle').textContent"), StringComparison.Ordinal);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_publishes_and_reloads_a_server_cataloged_schedule_graph), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Browser_preserves_server_owned_profile_fallback_order_override_conflicts_and_safe_text()
    {
        const string SecondaryProfileId = "org.example/model-profile/secondary";
        const string TertiaryProfileId = "org.example/model-profile/tertiary";
        const string UnavailableProfileId = "org.example/model-profile/unavailable";
        const string UnsafePurpose = "Secondary <img data-model-profile-xss src=x onerror=window.__modelProfileXss=true> model profile.";
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test", "gpt-secondary", "gpt-tertiary");
        var capabilityTrustRoot = Path.Combine(workspace.ServerStatePath, "browser-profile-capability-catalog");
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profileSpecs = new[]
        {
            new BrowserModelProfileSpec(SecondaryProfileId, "secondary", UnsafePurpose, "gpt-secondary", true),
            new BrowserModelProfileSpec(TertiaryProfileId, "tertiary", "Tertiary browser model profile.", "gpt-tertiary", true),
            new BrowserModelProfileSpec(UnavailableProfileId, "unavailable", "Unavailable browser model profile.", "gpt-unavailable", false),
        };
        var descriptors = profileSpecs.Select(BrowserProfileWebHost.CreateDescriptor).ToArray();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, descriptors);
        var authoringRole = await CreateScheduleGraphAuthoringRoleAsync(
            new WorkspacePaths(workspace.RootPath),
            descriptors.Select(descriptor => descriptor.Id.Value));
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(
            workspace.RootPath,
            GetFreePort(),
            codexExecutable,
            "gpt-test",
            capabilityTrustRoot,
            profileSpecs);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        HeadlessBrowserSession? staleBrowser = null;

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.EvaluateAsync("window.__modelProfileXss = false");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop')");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphModelProfile').textContent.includes('gpt-secondary') && document.getElementById('governedGraphModelProfile').textContent.includes('gpt-tertiary') && document.getElementById('governedGraphModelProfile').textContent.includes('gpt-unavailable')");
            Assert.True(await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#governedGraphModelProfile option')].some((option) => option.value === 'org.example/model-profile/unavailable' && option.disabled && option.textContent.toLowerCase().includes('unavailable'))"));
            Assert.False(await browser.EvaluateBooleanAsync("window.__modelProfileXss || Boolean(document.querySelector('[data-model-profile-xss]'))"));

            var authoringRoleValue = $"{authoringRole.Identity.RoleId}:{authoringRole.Identity.Revision}:{authoringRole.ContentHash}";
            await SetValueAsync(browser, "#governedGraphRole", authoringRoleValue, "change");
            await SetValueAsync(browser, "#governedGraphModelRoutingMode", "exact", "change");
            await SetValueAsync(browser, "#governedGraphModelProfile", BuiltInCapabilityCatalog.CodexModelProfileCapabilityId, "change");
            await SetValueAsync(browser, "#governedGraphId", "browser-profile-routing-graph");
            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-1");
            await SetValueAsync(browser, "#governedGraphDisplayName", "Browser profile routing graph");
            await SetValueAsync(browser, "#governedGraphPurpose", "Preserve exact server-owned routing evidence.");
            await ClickAsync(browser, "#governedGraphNewButton");
            await browser.EvaluateAsync("(() => { const selected = new Set(['org.example/model-profile/secondary', 'org.example/model-profile/tertiary']); const control = document.getElementById('governedGraphFallbackProfiles'); for (const option of control.options) option.selected = selected.has(option.value); control.dispatchEvent(new Event('change', { bubbles: true })); })()");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphFallbackOrder').textContent.includes('1. org.example/model-profile/secondary') && document.getElementById('governedGraphFallbackOrder').textContent.includes('2. org.example/model-profile/tertiary')");
            await browser.EvaluateWithUserGestureAsync("document.querySelector('#governedGraphFallbackOrder li:first-child button:last-child').click()");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/tertiary') < document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/secondary')");

            var forgedStatus = await browser.EvaluateInt32Async("(async () => { const catalog = await fetch('/api/governed-graphs/catalog').then((response) => response.json()); const profile = catalog.modelProfiles.profiles.find((item) => item.profileId === 'org.embodysense/model-profile/codex'); const response = await fetch('/api/model-profiles/preview', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ policy: profile.recommendedExactPolicy, roleId: 'schedule-graph-author', nodeTypeId: 'provider-inference', authoredInputDataClasses: null, metadata: { modelId: 'forged-browser-model' } }) }); return response.status; })()");
            Assert.Equal(400, forgedStatus);
            Assert.Equal(BuiltInCapabilityCatalog.CodexModelProfileCapabilityId, await browser.EvaluateStringAsync("document.getElementById('governedGraphModelProfile').value"));

            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "schedule-trigger");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "provider-inference");
            await SetValueAsync(browser, "#governedGraphInspector input:not([type='number'])", "Return exact profile routing evidence.");
            await browser.EvaluateAsync("(() => { const label = [...document.querySelectorAll('#governedGraphInspector label')].find((item) => item.querySelector('span')?.textContent === 'Model routing override'); const control = label?.querySelector('select'); if (!control) throw new Error('Model routing override was not rendered.'); control.value = 'org.example/model-profile/secondary'; control.dispatchEvent(new Event('change', { bubbles: true })); })()");
            await browser.WaitForExpressionAsync("[...document.querySelectorAll('#governedGraphInspector label')].find((item) => item.querySelector('span')?.textContent === 'Model routing override')?.querySelector('select')?.value === 'org.example/model-profile/secondary'");
            await browser.EvaluateAsync("(() => { const label = [...document.querySelectorAll('#governedGraphInspector label')].find((item) => item.querySelector('span')?.textContent === 'Ordered override fallbacks'); const control = label?.querySelector('select'); if (!control) throw new Error('Override fallbacks were not rendered.'); for (const option of control.options) option.selected = option.value === 'org.example/model-profile/tertiary'; control.dispatchEvent(new Event('change', { bubbles: true })); })()");
            await browser.WaitForExpressionAsync("[...document.querySelectorAll('#governedGraphInspector label')].find((item) => item.querySelector('span')?.textContent === 'Ordered override fallbacks')?.querySelector('select option[value=\"org.example/model-profile/tertiary\"]')?.selected && document.getElementById('governedGraphInspector').textContent.includes('org.example/model-profile/tertiary')");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "success-exit");
            await AddGovernedGraphControlAsync(browser, "schedule-trigger", "provider-inference", "Always");
            await AddGovernedGraphBindingAsync(browser, "schedule-trigger", "provider-inference", "Data · request → request");
            await AddGovernedGraphBindingAsync(browser, "schedule-trigger", "provider-inference", "Context · invocation-context → invocation-context");
            await AddGovernedGraphControlAsync(browser, "provider-inference", "success-exit", "Success");
            await AddGovernedGraphBindingAsync(browser, "provider-inference", "success-exit", "Data · result → result");
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphSaveButton').disabled");
            await ClickAsync(browser, "#governedGraphSaveButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Draft') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");

            staleBrowser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
            await staleBrowser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(staleBrowser, "#loopsNav");
            await staleBrowser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(staleBrowser, "#governedGraphTab");
            await staleBrowser.WaitForExpressionAsync("document.getElementById('governedGraphRole').options.length > 0 && document.getElementById('governedGraphModelProfile').options.length >= 4");
            await SetValueAsync(staleBrowser, "#governedGraphId", "browser-profile-routing-graph");
            await staleBrowser.WaitForExpressionAsync("!document.getElementById('governedGraphLoadButton').disabled");
            await ClickAsync(staleBrowser, "#governedGraphLoadButton");
            await staleBrowser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Draft') && document.getElementById('governedGraphDisplayName').value === 'Browser profile routing graph'");

            await SetValueAsync(staleBrowser, "#governedGraphRole", authoringRoleValue, "change");
            var staleRoleState = await staleBrowser.EvaluateStringAsync("JSON.stringify({ value: document.getElementById('governedGraphRole').value, options: [...document.getElementById('governedGraphRole').options].map((option) => ({ value: option.value, disabled: option.disabled, text: option.textContent })) })");
            Assert.Contains(authoringRoleValue, staleRoleState, StringComparison.Ordinal);
            Assert.True(await staleBrowser.EvaluateBooleanAsync("Boolean(document.getElementById('governedGraphRole').selectedOptions[0]) && !document.getElementById('governedGraphRole').selectedOptions[0].disabled"), staleRoleState);
            await SetValueAsync(staleBrowser, "#governedGraphRevisionId", "revision-stale-2");
            await SetValueAsync(staleBrowser, "#governedGraphDisplayName", "Stale browser replacement");
            await staleBrowser.WaitForExpressionAsync("!document.getElementById('governedGraphSaveButton').disabled");
            // Deliberately hold the stale author's request until the current author commits, then prove the stale mutation receives a conflict; see https://github.com/Jacob-J-Thomas/agenthome-poc/issues/417.
            await staleBrowser.EvaluateAsync("(() => { const original = window.fetch.bind(window); window.__originalStaleFetch = original; window.fetch = (url, options) => { if (String(url).endsWith('/api/governed-graphs/mutate')) { window.__staleMutation = { url, options }; return new Promise((resolve) => { window.__resolveStaleMutation = resolve; }); } return original(url, options); }; })()");
            await ClickAsync(staleBrowser, "#governedGraphSaveButton");
            await staleBrowser.WaitForExpressionAsync("Boolean(window.__staleMutation?.options?.body) && typeof window.__resolveStaleMutation === 'function'");

            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-2");
            await SetValueAsync(browser, "#governedGraphPurpose", "Current tab owns this exact replacement.");
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphSaveButton').disabled && document.getElementById('governedGraphRevisionId').value === 'revision-2' && document.getElementById('governedGraphPurpose').value === 'Current tab owns this exact replacement.'");
            await ClickAsync(browser, "#governedGraphSaveButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphNotice').textContent.includes('Committed') && document.getElementById('governedGraphPurpose').value === 'Current tab owns this exact replacement.'");
            var staleMutationResult = await staleBrowser.EvaluateStringAsync("(async () => { const response = await window.__originalStaleFetch(window.__staleMutation.url, window.__staleMutation.options); const clone = response.clone(); window.__resolveStaleMutation(response); return JSON.stringify({ status: clone.status, body: await clone.text() }); })()");
            using (var staleMutationDocument = JsonDocument.Parse(staleMutationResult))
            {
                Assert.True(
                    staleMutationDocument.RootElement.GetProperty("status").GetInt32() == 409,
                    staleMutationDocument.RootElement.GetProperty("body").GetString());
            }
            await staleBrowser.WaitForExpressionAsync("document.getElementById('governedGraphNotice').textContent.toLowerCase().includes('conflict')");
            Assert.Contains("conflict", await staleBrowser.EvaluateStringAsync("document.getElementById('governedGraphNotice').textContent"), StringComparison.OrdinalIgnoreCase);
            Assert.False(await staleBrowser.EvaluateBooleanAsync("Object.keys(sessionStorage).some((key) => key.includes('governed-graph-pending-mutation') && sessionStorage.getItem(key))"));
            await staleBrowser.DisposeAsync();
            staleBrowser = null;

            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphPublishButton').disabled");
            await ClickAsync(browser, "#governedGraphPublishButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.querySelectorAll('#governedGraphCanvas .governed-graph-node').length === 3");
            Assert.Contains("org.example/model-profile/tertiary", await browser.EvaluateStringAsync("document.getElementById('governedGraphFallbackOrder').textContent"), StringComparison.Ordinal);
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/tertiary') < document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/secondary')"));
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "provider-inference");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('org.example/model-profile/secondary') && document.getElementById('governedGraphInspector').textContent.includes('org.example/model-profile/tertiary') && document.getElementById('governedGraphInspector').textContent.includes('Eligible')");
            Assert.Equal("Current tab owns this exact replacement.", await browser.EvaluateStringAsync("document.getElementById('governedGraphPurpose').value"));
            Assert.NotEqual("Stale browser replacement", await browser.EvaluateStringAsync("document.getElementById('governedGraphDisplayName').value"));
            Assert.False(await browser.EvaluateBooleanAsync("window.__modelProfileXss || Boolean(document.querySelector('[data-model-profile-xss]'))"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync("/api/model-profiles/preview");
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_preserves_server_owned_profile_fallback_order_override_conflicts_and_safe_text), browser, app);
            throw;
        }
        finally
        {
            if (staleBrowser is not null)
            {
                await staleBrowser.DisposeAsync();
            }
        }
    }

    [InstalledBrowserFact]
    public async Task Loops_deep_link_deliberately_initializes_an_empty_workspace_without_creating_or_running_a_custom_loop()
    {
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test");
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl + "/?view=loops");

        try
        {
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && !document.getElementById('loopInitializationPanel').hidden");
            var expectedRoot = JsonSerializer.Serialize(workspace.RootPath);
            await browser.WaitForExpressionAsync($"document.getElementById('loopInitializationRoot').textContent === {expectedRoot}");
            Assert.Equal(workspace.RootPath, await browser.EvaluateStringAsync("document.getElementById('loopInitializationRoot').textContent"));
            var explanation = await browser.EvaluateStringAsync("document.getElementById('loopInitializationPanel').textContent");
            Assert.Contains(".agent/", explanation, StringComparison.Ordinal);
            Assert.Contains("private/", explanation, StringComparison.Ordinal);
            Assert.Contains("protected seed documents", explanation, StringComparison.Ordinal);
            Assert.Contains("No custom loop is created", explanation, StringComparison.Ordinal);
            Assert.Contains("no loop or model inference runs", explanation, StringComparison.Ordinal);
            Assert.True(await browser.EvaluateBooleanAsync("!document.getElementById('initializeLoopsWorkspaceButton').disabled"));

            await ClickAsync(browser, "#initializeLoopsWorkspaceButton");
            await browser.WaitForExpressionAsync("document.getElementById('loopInitializationPanel').hidden && !document.getElementById('createLoopButton').disabled");
            await browser.WaitForExpressionAsync("document.getElementById('loopList').textContent.includes('System loop')");

            Assert.True(File.Exists(workspace.File(".agent", "ROLE.md")));
            Assert.True(File.Exists(workspace.File(".agent", "permissions.json")));
            Assert.Equal(0, await GetCustomDefinitionCountAsync(browser));
            var customRunPath = workspace.File(".agent", "loops", "runs", "custom");
            Assert.False(Directory.Exists(customRunPath) && Directory.EnumerateFiles(customRunPath, "*", SearchOption.AllDirectories).Any());
            Assert.Contains("initialization completed", await browser.EvaluateStringAsync("document.getElementById('loopInitializationAnnouncement').textContent"), StringComparison.OrdinalIgnoreCase);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Loops_deep_link_deliberately_initializes_an_empty_workspace_without_creating_or_running_a_custom_loop), browser, app);
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
    public async Task Browser_inspects_and_confirms_an_exact_capability_lifecycle_preview()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForWeb().InitializeAsync(workspace.RootPath);
        var capabilityId = await InstallBrowserLifecycleCapabilityAsync(workspace.RootPath);
        var capabilityIdJson = JsonSerializer.Serialize(capabilityId.Value);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test");
        HeadlessBrowserSession? browser = null;

        try
        {
            browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl + "/capabilities.html");
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("document.getElementById('capabilityList').textContent.includes(" + capabilityIdJson + ")");
            await browser.EvaluateWithUserGestureAsync("(() => { const item = [...document.querySelectorAll('#capabilityList .capability-list-item')].find((candidate) => candidate.textContent.includes(" + capabilityIdJson + ")); if (!item) throw new Error('Browser lifecycle capability was not rendered.'); item.click(); })()");
            await browser.WaitForExpressionAsync("document.getElementById('capabilityTitle').textContent === " + capabilityIdJson);
            await browser.WaitForExpressionAsync("document.getElementById('capabilityPurpose').textContent.includes('Browser lifecycle E2E capability')");

            var purpose = await browser.EvaluateStringAsync("document.getElementById('capabilityPurpose').textContent");
            var detail = await browser.EvaluateStringAsync("document.getElementById('capabilityContent').textContent");
            Assert.Contains("Browser lifecycle E2E capability", purpose, StringComparison.Ordinal);
            Assert.Contains("No registered loop, skill, or package currently depends", detail, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.RootPath, detail, StringComparison.Ordinal);
            Assert.DoesNotContain("secretValue", detail, StringComparison.OrdinalIgnoreCase);

            await SetValueAsync(browser, "#lifecycleOperation", "disable", "change");
            await ClickAsync(browser, "#previewLifecycleButton");
            await browser.WaitForExpressionAsync("(() => { const confirm = [...document.querySelectorAll('#lifecyclePreview button')].find((button) => button.textContent.includes('Confirm Disable')); return !document.getElementById('lifecyclePreview').hidden && confirm && !confirm.disabled; })()");
            var storageKey = await browser.EvaluateStringAsync("Object.keys(localStorage).find((key) => key.startsWith('embodysense.pending-capability-lifecycle.v1.'))");
            Assert.Matches("^embodysense\\.pending-capability-lifecycle\\.v1\\.[0-9a-f]{64}$", storageKey);
            Assert.DoesNotContain(workspace.RootPath, storageKey, StringComparison.Ordinal);
            var discardedOperationId = await browser.EvaluateStringAsync("JSON.parse(localStorage.getItem(Object.keys(localStorage).find((key) => key.startsWith('embodysense.pending-capability-lifecycle.v1.')))).entries[0].selection.operationId");
            Assert.StartsWith("web-capability-", discardedOperationId, StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#lifecyclePreview button", "Discard preview");
            await browser.WaitForExpressionAsync("document.getElementById('lifecycleNotice').textContent.includes('Discarded')");
            Assert.True(await browser.EvaluateBooleanAsync("JSON.parse(localStorage.getItem(Object.keys(localStorage).find((key) => key.startsWith('embodysense.pending-capability-lifecycle.v1.')))).entries.length === 0"));

            await ClickAsync(browser, "#previewLifecycleButton");
            await browser.WaitForExpressionAsync("(() => { const confirm = [...document.querySelectorAll('#lifecyclePreview button')].find((button) => button.textContent.includes('Confirm Disable')); return !document.getElementById('lifecyclePreview').hidden && confirm && !confirm.disabled; })()");
            var pendingOperationId = await browser.EvaluateStringAsync("JSON.parse(localStorage.getItem(Object.keys(localStorage).find((key) => key.startsWith('embodysense.pending-capability-lifecycle.v1.')))).entries[0].selection.operationId");
            Assert.StartsWith("web-capability-", pendingOperationId, StringComparison.Ordinal);
            Assert.NotEqual(discardedOperationId, pendingOperationId);

            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('lifecyclePreview').textContent.includes(" + JsonSerializer.Serialize(pendingOperationId) + ")");
            Assert.Equal(pendingOperationId, await browser.EvaluateStringAsync("JSON.parse(localStorage.getItem(Object.keys(localStorage).find((key) => key.startsWith('embodysense.pending-capability-lifecycle.v1.')))).entries[0].selection.operationId"));
            await browser.EvaluateAsync("window.confirm = () => true");
            await ClickButtonByTextAsync(browser, "#lifecyclePreview button", "Confirm Disable");

            await browser.WaitForExpressionAsync("document.getElementById('capabilityBadges').textContent.includes('Disabled')");
            Assert.True(await browser.EvaluateBooleanAsync("JSON.parse(localStorage.getItem(Object.keys(localStorage).find((key) => key.startsWith('embodysense.pending-capability-lifecycle.v1.')))).entries.length === 0"));
            Assert.Contains("Applied", await browser.EvaluateStringAsync("document.getElementById('lifecycleNotice').textContent"), StringComparison.Ordinal);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_inspects_and_confirms_an_exact_capability_lifecycle_preview), browser, app);
            throw;
        }
        finally
        {
            if (browser is not null)
            {
                await browser.DisposeAsync();
            }
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

    private static async Task<CapabilityId> InstallBrowserLifecycleCapabilityAsync(string workspaceRoot)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        var catalogTrust = FileCapabilityCatalogTrustProvider.CreateDefault();
        var artifactTrust = new FileCapabilityArtifactStateTrustProvider(catalogTrust.RootPath);
        var content = "browser-lifecycle-artifact"u8.ToArray();
        var digest = CapabilityIntegrityDigest.Compute(content);
        Assert.True(CapabilityId.TryParse("org.example/browser-lifecycle", out var id, out _));
        Assert.True(CapabilityProviderId.TryParse("org.example", out var provider, out _));
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var version, out _));
        Assert.True(CapabilityVersionRange.TryParse("*", out var range, out _));
        Assert.True(CapabilityJsonSchema.TryCreate($"{{\"$schema\":\"{CapabilityJsonSchema.Draft202012Dialect}\",\"type\":\"object\"}}", out var schema, out _));
        const string SourceUri = "file:///sources/browser-lifecycle";
        var descriptor = new CapabilityDescriptor(1, id!, CapabilityKind.Skill, version!, new CapabilityImplementationIdentity(provider!, "browser-lifecycle"), new CapabilityProvenance(CapabilityProvenanceKind.LocalSource, SourceUri, "rev-1", digest), new CapabilityCompatibility(range!, [CapabilityHostRuntime.Platform]), "Browser lifecycle E2E capability.", schema!, schema!, new CapabilityResourceLimits(1_000, 32_000_000, 16_384, 1), CapabilitySideEffectClass.None, new CapabilityAccessRequirements([], CapabilityEgressMode.None, [], []));
        var manifest = new CapabilityArtifactManifest(1, descriptor, new CapabilityArtifactSourceReference(CapabilityArtifactSourceKind.Local, SourceUri, "rev-1", CapabilityArtifactUpdatePolicy.Pinned), digest, null, CapabilityHostRuntime.Platform, "browser-lifecycle", []);
        var stage = new CapabilityArtifactStageRequest(manifest, new CapabilityArtifactContent(content), new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "browser-e2e-policy", "Verified."));
        var catalog = new CapabilityCatalogService(new CapabilityCatalogStore(paths, catalogTrust));
        var revision = (await catalog.ReadAsync(null, 1)).Page!.CatalogRevision;
        revision = (await catalog.DeclareAsync(descriptor, revision, "declare-browser-lifecycle")).CatalogRevision!.Value;
        revision = (await catalog.InstallAsync(descriptor.Id, revision, "install-browser-lifecycle")).CatalogRevision!.Value;
        revision = (await catalog.VerifyAsync(descriptor.Id, revision, "verify-browser-lifecycle")).CatalogRevision!.Value;
        revision = (await catalog.EnableAsync(descriptor.Id, revision, "enable-browser-lifecycle")).CatalogRevision!.Value;
        Assert.Equal(CapabilityCatalogMutationStatus.Applied, (await catalog.MarkHealthyAsync(descriptor.Id, revision, "healthy-browser-lifecycle")).Status);
        var artifacts = new CapabilityArtifactStore(paths, artifactTrust, BrowserCapabilityArtifactVerifier.Instance);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.StageAsync(stage)).Status);
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifacts.ActivateAsync(new CapabilityArtifactActivationRequest(manifest, 0, "activate-browser-lifecycle"))).Status);
        return descriptor.Id;
    }

    private static async Task InitializeWorkspaceAsync(HeadlessBrowserSession browser)
    {
        await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Needs initialization')");
        await browser.WaitForExpressionAsync("!document.getElementById('initButton').disabled");
        await ClickAsync(browser, "#initButton");
        await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
        await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
    }

    private static Task<int> GetCustomDefinitionCountAsync(HeadlessBrowserSession browser)
    {
        const string Expression = "(async () => { const response = await fetch('/api/loops', { cache: 'no-store' }); if (!response.ok) throw new Error(`Loop catalog request failed with HTTP ${response.status}.`); const catalog = await response.json(); if (!Array.isArray(catalog.customDefinitions)) throw new Error('Loop catalog did not expose custom definitions.'); return catalog.customDefinitions.length; })()";
        return browser.EvaluateInt32Async(Expression);
    }

    private static async Task SubmitMessageAsync(HeadlessBrowserSession browser, string message)
    {
        var jsonMessage = JsonSerializer.Serialize(message);
        await browser.EvaluateAsync("(() => { const input = document.getElementById('messageInput'); const send = document.getElementById('sendButton'); const cancel = document.getElementById('cancelButton'); input.value = " + jsonMessage + "; document.getElementById('messageForm').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })); if (input.value !== '' || !send.disabled || cancel.disabled) throw new Error('The browser did not synchronously accept the submitted turn.'); })()");
    }

    private static async Task AssertChatRequestRegistryEmptyAsync(HeadlessBrowserSession browser)
    {
        const string Expression = "(() => { const prefix = 'embodysense.chat-requests.v1'; const keys = Object.keys(localStorage).filter((key) => key.startsWith(prefix + '.')); if (keys.length !== 1 || localStorage.getItem(prefix) !== null) return false; const scope = keys[0].slice(prefix.length + 1); const raw = localStorage.getItem(keys[0]); if (!raw) return false; const registry = JSON.parse(raw); return Object.keys(registry).sort().join(',') === 'entries,schemaVersion,scope' && registry.schemaVersion === 1 && /^[0-9a-f]{64}$/.test(scope) && registry.scope === scope && Array.isArray(registry.entries) && registry.entries.length === 0 && !raw.includes('access_token'); })()";
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
        await browser.EvaluateWithUserGestureAsync("(() => { const item = [...document.querySelectorAll('#loopList .loop-list-item')].find((candidate) => candidate.textContent.includes(" + jsonName + ")); if (!item) throw new Error('Loop was not rendered: ' + " + jsonName + "); item.click(); })()");
        await browser.WaitForExpressionAsync("document.getElementById('loopName').value === " + jsonName);
    }

    private static async Task ClickButtonByTextAsync(HeadlessBrowserSession browser, string selector, string text)
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        var jsonText = JsonSerializer.Serialize(text);
        await browser.EvaluateWithUserGestureAsync("(() => { const button = [...document.querySelectorAll(" + jsonSelector + ")].find((candidate) => candidate.textContent.includes(" + jsonText + ")); if (!button) throw new Error('Button was not rendered: ' + " + jsonText + "); button.click(); })()");
    }

    private static async Task AddGovernedGraphControlAsync(
        HeadlessBrowserSession browser,
        string fromNodeId,
        string toNodeId,
        string outcome)
    {
        await SetValueAsync(browser, "#governedGraphConnectionFrom", fromNodeId, "change");
        await SetValueAsync(browser, "#governedGraphConnectionTo", toNodeId, "change");
        await SetValueAsync(browser, "#governedGraphControlCondition", outcome.ToLowerInvariant(), "change");
        await ClickAsync(browser, "#governedGraphAddControlButton");
    }

    private static async Task AddGovernedGraphBindingAsync(
        HeadlessBrowserSession browser,
        string fromNodeId,
        string toNodeId,
        string bindingText)
    {
        await SetValueAsync(browser, "#governedGraphConnectionFrom", fromNodeId, "change");
        await SetValueAsync(browser, "#governedGraphConnectionTo", toNodeId, "change");
        var jsonText = JsonSerializer.Serialize(bindingText);
        await browser.EvaluateAsync("(() => { const select = document.getElementById('governedGraphBindingChoice'); const option = [...select.options].find((candidate) => candidate.textContent.includes(" + jsonText + ")); if (!option) throw new Error('Typed binding was not rendered: ' + " + jsonText + "); select.value = option.value; select.dispatchEvent(new Event('change', { bubbles: true })); })()");
        await ClickAsync(browser, "#governedGraphAddBindingButton");
    }

    private static async Task ClickAsync(HeadlessBrowserSession browser, string selector)
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        await browser.EvaluateWithUserGestureAsync("(() => { const element = document.querySelector(" + jsonSelector + "); if (!element) throw new Error('Element was not rendered: ' + " + jsonSelector + "); element.click(); })()");
    }

    private static async Task SetValueAsync(HeadlessBrowserSession browser, string selector, string value, string eventName = "input")
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        var jsonValue = JsonSerializer.Serialize(value);
        var jsonEventName = JsonSerializer.Serialize(eventName);
        await browser.EvaluateAsync("(() => { const element = document.querySelector(" + jsonSelector + "); if (!element) throw new Error('Element was not rendered: ' + " + jsonSelector + "); element.value = " + jsonValue + "; element.dispatchEvent(new Event(" + jsonEventName + ", { bubbles: true })); })()");
    }

    private static async Task InstallBrowserModelProfilesAsync(
        string workspaceRoot,
        string capabilityTrustRoot,
        IEnumerable<CapabilityDescriptor> descriptors)
    {
        var catalog = new CapabilityCatalogService(new CapabilityCatalogStore(
            new WorkspacePaths(workspaceRoot),
            new FileCapabilityCatalogTrustProvider(capabilityTrustRoot)));
        var read = await catalog.ReadAsync(null, 1);
        Assert.Equal(CapabilityCatalogReadStatus.Available, read.Status);
        var revision = Assert.IsType<long>(read.Page?.CatalogRevision);
        foreach (var descriptor in descriptors)
        {
            revision = RequireApplied(await catalog.DeclareAsync(descriptor, revision, $"declare-{descriptor.Implementation.ImplementationId.Replace('/', '-')}"));
            revision = RequireApplied(await catalog.InstallAsync(descriptor.Id, revision, $"install-{descriptor.Implementation.ImplementationId.Replace('/', '-')}"));
            revision = RequireApplied(await catalog.VerifyAsync(descriptor.Id, revision, $"verify-{descriptor.Implementation.ImplementationId.Replace('/', '-')}"));
            revision = RequireApplied(await catalog.EnableAsync(descriptor.Id, revision, $"enable-{descriptor.Implementation.ImplementationId.Replace('/', '-')}"));
            revision = RequireApplied(await catalog.MarkHealthyAsync(descriptor.Id, revision, $"healthy-{descriptor.Implementation.ImplementationId.Replace('/', '-')}"));
        }

        static long RequireApplied(CapabilityCatalogMutationResult result)
        {
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
            return Assert.IsType<long>(result.CatalogRevision);
        }
    }

    private static async Task<ContextualRoleRevisionPin> CreateScheduleGraphAuthoringRoleAsync(
        WorkspacePaths paths,
        IEnumerable<string>? additionalCapabilityIds = null)
    {
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var maximumCapabilityIds = new[]
        {
            "org.embodysense/conversation-turn",
            "org.embodysense/model-inference",
            BuiltInCapabilityCatalog.CodexModelProfileCapabilityId,
            "org.embodysense/triggers/time",
        }
        .Concat(additionalCapabilityIds ?? [])
        .Distinct(StringComparer.Ordinal)
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToImmutableArray();
        var revision = ContextualRoleRevisionContentHash.Apply(new ContextualRoleRevision(
            ContextualRoleLimits.SchemaVersion,
            new ContextualRoleRevisionIdentity("schedule-graph-author", 1),
            string.Empty,
            "Schedule graph author",
            "Author one bounded scheduled inference graph through the installed-browser journey.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("browser-e2e", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new ContextualRoleWorkspaceApplicability(ImmutableArray.Create(workspaceId)),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.WorkspaceRoleMarkdown,
                "role",
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(maximumCapabilityIds)));
        var request = ContextualRoleRevisionMutationRequestHash.Apply(new ContextualRoleRevisionMutationRequest(
            "create-schedule-graph-author",
            string.Empty,
            ContextualRoleRevisionMutationKind.Create,
            revision.Identity.RoleId,
            "browser-e2e",
            revision,
            null,
            DateTimeOffset.UnixEpoch));
        using var store = new ContextualRoleRevisionStore(paths, workspaceId);
        var result = await store.MutateAsync(request);
        Assert.Equal(ContextualRoleRevisionMutationStatus.Accepted, result.Status);
        var persisted = Assert.IsType<ContextualRoleRevision>(result.Revision);
        Assert.Equal(revision.ContentHash, persisted.ContentHash);
        return new ContextualRoleRevisionPin(persisted.Identity, persisted.ContentHash);
    }

    private static async Task<string> ReadConversationEvidenceAsync(TestWorkspace workspace)
    {
        var snapshot = await new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath)).LoadConversationHistorySnapshotAsync(50, 400, 4_000_000);
        return string.Join(Environment.NewLine, snapshot.Transcripts.SelectMany(transcript => transcript.Lines));
    }

    private static async Task WriteFailureDiagnosticsAsync(string scenario, HeadlessBrowserSession? browser, ExternalWebApplicationProcess? app, string? retiredServerOutput = null)
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

        if (!string.IsNullOrWhiteSpace(retiredServerOutput))
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "retired-server-output.txt"), retiredServerOutput);
        }
    }

    private sealed class BrowserCapabilityArtifactVerifier : ICapabilityArtifactTrustVerifier
    {
        public static BrowserCapabilityArtifactVerifier Instance { get; } = new();

        public Task<CapabilityArtifactTrustDecision> VerifyAsync(CapabilityArtifactManifest manifest, CapabilityIntegrityDigest actualDigest, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "browser-e2e-policy", "Verified."));
        }
    }

    private sealed class BrowserServerAccountDirectory : IDisposable
    {
        public BrowserServerAccountDirectory(string fallbackRoot)
        {
            RootPath = OperatingSystem.IsMacOS()
                ? Path.Combine(AppContext.BaseDirectory, "browser-server-accounts", Guid.NewGuid().ToString("N"))
                : Path.Combine(fallbackRoot, "account-home");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
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
        private readonly ConcurrentDictionary<string, byte> _expectedServerRestartRequests = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _requestUrls = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _diagnosticsGate = new();
        private readonly List<string> _diagnostics = [];
        private readonly byte[] _buffer = new byte[65536];
        private readonly Task _readerTask;
        private readonly string _targetAuthority;
        private Exception? _readerFailure;
        private int _acceptNextJavaScriptDialog;
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

        public async Task EvaluateWithUserGestureAsync(string expression)
        {
            _ = await EvaluateAsync(expression, CancellationToken.None, userGesture: true);
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

        public async Task ReloadAsync(bool acceptBeforeUnload = false)
        {
            if (acceptBeforeUnload)
            {
                Interlocked.Exchange(ref _acceptNextJavaScriptDialog, 1);
            }

            try
            {
                _ = await SendCommandAsync("Page.reload", new { ignoreCache = true });
            }
            catch
            {
                if (acceptBeforeUnload)
                {
                    Interlocked.CompareExchange(ref _acceptNextJavaScriptDialog, 0, 1);
                }

                throw;
            }
        }

        public void BeginExpectedServerRestart()
        {
            _expectedServerRestartRequests.Clear();
            Interlocked.Exchange(ref _expectedServerRestart, 1);
        }

        public void MarkExpectedReplacementServerStarting()
        {
            Interlocked.CompareExchange(ref _expectedServerRestart, 2, 1);
        }

        public void EndExpectedServerRestart()
        {
            Interlocked.Exchange(ref _expectedServerRestart, 0);
        }

        public async Task AssertHealthyAsync(params string[] expectedErrorUrlFragments)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = await EvaluateAsync("true", timeout.Token);
            Assert.False(_process.HasExited, $"Browser process exited unexpectedly.{Environment.NewLine}{FormatOutput()}");
            Assert.Null(_readerFailure);
            var diagnostics = GetDiagnosticsSnapshot().ToList();
            foreach (var fragment in expectedErrorUrlFragments)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(fragment);
                var removed = diagnostics.RemoveAll(item => item.Contains(fragment, StringComparison.Ordinal));
                Assert.True(removed > 0, $"The expected browser HTTP failure `{fragment}` was not observed.");
            }
            Assert.Empty(diagnostics);
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

        private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken, bool userGesture = false)
        {
            var response = await SendCommandAsync("Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture
            }, cancellationToken);
            if (response.TryGetProperty("exceptionDetails", out var exceptionDetails))
            {
                throw new InvalidOperationException("Browser evaluation failed: " + exceptionDetails.GetRawText());
            }

            if (!response.TryGetProperty("result", out var commandResult)
                || !commandResult.TryGetProperty("result", out var remoteObject))
            {
                var detail = response.TryGetProperty("error", out var error)
                    ? error.GetRawText()
                    : response.GetRawText();
                throw new InvalidOperationException("Browser evaluation command failed: " + detail);
            }

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

            if (method == "Page.javascriptDialogOpening" && Interlocked.Exchange(ref _acceptNextJavaScriptDialog, 0) == 1)
            {
                _ = AcceptJavaScriptDialogAsync();
                return;
            }

            if (method == "Page.frameNavigated")
            {
                Interlocked.Exchange(ref _acceptNextJavaScriptDialog, 0);
            }

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
                if (IsExpectedServerRestartHttpResponse(response, statusCode))
                {
                    return;
                }

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
                var url = requestUrl.GetString()!;
                _requestUrls[requestId] = url;
                CaptureExpectedServerRestartRequest(requestId, url);
                return;
            }

            if (method == "Network.webSocketCreated"
                && parameters.TryGetProperty("url", out var websocketUrl)
                && websocketUrl.ValueKind == JsonValueKind.String)
            {
                var url = websocketUrl.GetString()!;
                _requestUrls[requestId] = url;
                CaptureExpectedServerRestartRequest(requestId, url);
                return;
            }

            if (method is "Network.loadingFinished" or "Network.webSocketClosed")
            {
                _requestUrls.TryRemove(requestId, out _);
                _expectedServerRestartRequests.TryRemove(requestId, out _);
            }
        }

        private bool IsExpectedServerRestartLogEntry(JsonElement entry)
        {
            var requestId = entry.TryGetProperty("networkRequestId", out var requestIdValue) && requestIdValue.ValueKind == JsonValueKind.String
                ? requestIdValue.GetString()
                : null;
            var beganDuringOutage = requestId is not null && _expectedServerRestartRequests.ContainsKey(requestId);
            if (Volatile.Read(ref _expectedServerRestart) == 0 && !beganDuringOutage)
            {
                return false;
            }

            var source = entry.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() : null;
            var text = entry.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
            var url = entry.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            if (!string.Equals(source, "network", StringComparison.Ordinal) || !ContainsTargetAuthority(text) && !ContainsTargetAuthority(url))
            {
                return false;
            }

            var expected = text?.Contains("401 (Unauthorized)", StringComparison.OrdinalIgnoreCase) == true
                || (text?.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) == true
                    || IsExpectedServerRestartUrl(url))
                && (text?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true || text?.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase) == true);
            if (expected && requestId is not null)
            {
                _expectedServerRestartRequests.TryRemove(requestId, out _);
            }

            return expected;
        }

        private bool IsExpectedServerRestartHttpResponse(JsonElement response, double statusCode)
        {
            return statusCode == 401
                && Volatile.Read(ref _expectedServerRestart) != 0
                && response.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && ContainsTargetAuthority(url.GetString());
        }

        private bool IsExpectedServerRestartNetworkFailure(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("requestId", out var requestIdValue)
                || requestIdValue.ValueKind != JsonValueKind.String
                || !_requestUrls.TryRemove(requestIdValue.GetString()!, out var requestUrl))
            {
                return false;
            }

            var beganDuringOutage = _expectedServerRestartRequests.ContainsKey(requestIdValue.GetString()!);
            if (Volatile.Read(ref _expectedServerRestart) == 0 && !beganDuringOutage
                || !Uri.TryCreate(requestUrl, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Authority, _targetAuthority, StringComparison.OrdinalIgnoreCase)
                || !IsExpectedServerRestartScheme(uri))
            {
                return false;
            }

            var errorText = parameters.TryGetProperty("errorText", out var errorTextValue) ? errorTextValue.GetString() : null;
            var expected = errorText?.Contains("ERR_CONNECTION_REFUSED", StringComparison.OrdinalIgnoreCase) == true
                || errorText?.Contains("failed", StringComparison.OrdinalIgnoreCase) == true;
            if (expected)
            {
                _expectedServerRestartRequests.TryRemove(requestIdValue.GetString()!, out _);
            }

            return expected;
        }

        private void CaptureExpectedServerRestartRequest(string requestId, string url)
        {
            if (Volatile.Read(ref _expectedServerRestart) == 1 && IsExpectedServerRestartUrl(url))
            {
                _expectedServerRestartRequests.TryAdd(requestId, 0);
            }
        }

        private bool ContainsTargetAuthority(string? value)
        {
            return value?.Contains(_targetAuthority, StringComparison.OrdinalIgnoreCase) == true;
        }

        private bool IsExpectedServerRestartUrl(string? value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && string.Equals(uri.Authority, _targetAuthority, StringComparison.OrdinalIgnoreCase)
                && IsExpectedServerRestartScheme(uri);
        }

        private static bool IsExpectedServerRestartScheme(Uri uri)
        {
            return string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase);
        }

        private async Task AcceptJavaScriptDialogAsync()
        {
            try
            {
                _ = await SendCommandAsync("Page.handleJavaScriptDialog", new { accept = true });
            }
            catch (Exception exception)
            {
                AddDiagnostic("expected browser dialog could not be accepted: " + exception.Message);
            }
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
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/usr/bin/microsoft-edge",
                "/usr/bin/google-chrome",
                "/usr/bin/chromium"
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
