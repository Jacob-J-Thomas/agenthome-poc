using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.CommandActions.Models;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Loops.ReceiptRetention;
using EmbodySense.Core.Application.Loops.ReceiptRetention.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.CommandActions;
using EmbodySense.Core.Persistence.ContextualRoles;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Models;
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

public sealed partial class BrowserFlowTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan _visibleGovernedInvocationTimeout = TimeSpan.FromSeconds(150);

    [Fact]
    public void Restart_diagnostic_classifier_accepts_connection_reset_only_for_expected_target_traffic()
    {
        const string TargetAuthority = "127.0.0.1:5001";

        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "ws://127.0.0.1:5001/hubs/session", "net::ERR_CONNECTION_RESET", TargetAuthority));
        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/api/session", "net::ERR_CONNECTION_RESET", TargetAuthority));
        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(false, true, "wss://127.0.0.1:5001/hubs/session", "net::ERR_CONNECTION_RESET", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(false, false, "ws://127.0.0.1:5001/hubs/session", "net::ERR_CONNECTION_RESET", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/", "net::ERR_CONNECTION_RESET", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/api/loops", "net::ERR_CONNECTION_RESET", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://example.test/api/session", "net::ERR_CONNECTION_RESET", TargetAuthority));
    }

    [Fact]
    public void Restart_diagnostic_classifier_allows_only_captured_same_authority_resets()
    {
        const string TargetAuthority = "127.0.0.1:5001";

        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "net::ERR_CONNECTION_RESET", TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "net::ERR_CONNECTION_REFUSED", TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "fetch failed", TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://example.test/api/loop-runs?maximumCount=50", "net::ERR_CONNECTION_RESET", TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedNetworkFailure(true, false, "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", "500 (Internal Server Error)", TargetAuthority, capturedAtRestart: true));
    }

    [Fact]
    public void Restart_log_classifier_matches_connection_reset_route_and_keeps_other_errors_visible()
    {
        const string TargetAuthority = "127.0.0.1:5001";

        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "WebSocket failed: net::ERR_CONNECTION_RESET", "ws://127.0.0.1:5001/hubs/session", null, TargetAuthority));
        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://127.0.0.1:5001/api/session", null, TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://127.0.0.1:5001/", null, TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "WebSocket failed: net::ERR_CONNECTION_RESET", "ws://example.test/hubs/session", null, TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "console", "WebSocket failed: net::ERR_CONNECTION_RESET", "ws://127.0.0.1:5001/hubs/session", null, TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "500 (Internal Server Error)", "https://127.0.0.1:5001/api/session", null, TargetAuthority));
        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "WebSocket failed: net::ERR_CONNECTION_RESET", null, "ws://127.0.0.1:5001/hubs/session", TargetAuthority));
        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://127.0.0.1:5001/", "https://127.0.0.1:5001/api/session", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "WebSocket failed: net::ERR_CONNECTION_RESET", null, "https://127.0.0.1:5001/api/loops", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "WebSocket failed: net::ERR_CONNECTION_RESET", null, "ws://example.test/hubs/session", TargetAuthority));
    }

    [Fact]
    public void Restart_log_classifier_allows_captured_reset_but_keeps_unrelated_diagnostics_visible()
    {
        const string TargetAuthority = "127.0.0.1:5001";

        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", null, TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", null, TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://example.test/api/loop-runs?maximumCount=50", null, TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://example.test/api/loop-runs?maximumCount=50", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", TargetAuthority, capturedAtRestart: true));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartLogEntry(true, false, "network", "500 (Internal Server Error)", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50", null, TargetAuthority, capturedAtRestart: true));
    }

    [Fact]
    public void Restart_page_exception_classifier_accepts_only_the_exact_active_recovery_abort()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        const string ExactDescription = "Error: The browser session is being recovered.\n    at suspendSession (https://127.0.0.1:5001/loop-builder.js:7023:7)";

        Assert.True(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught (in promise)", "Error", ExactDescription, "suspendSession", "https://127.0.0.1:5001/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(false, "Uncaught (in promise)", "Error", ExactDescription, "suspendSession", "https://127.0.0.1:5001/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught", "Error", ExactDescription, "suspendSession", "https://127.0.0.1:5001/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught (in promise)", "TypeError", ExactDescription, "suspendSession", "https://127.0.0.1:5001/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught (in promise)", "Error", ExactDescription, "resumeSession", "https://127.0.0.1:5001/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught (in promise)", "Error", "Error: The browser session is unavailable.", "suspendSession", "https://127.0.0.1:5001/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught (in promise)", "Error", ExactDescription, "suspendSession", "https://example.test/loop-builder.js", TargetAuthority));
        Assert.False(ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(true, "Uncaught (in promise)", "Error", ExactDescription, "suspendSession", "https://127.0.0.1:5001/app.js", TargetAuthority));
    }

    [Fact]
    public void Visible_run_readiness_requires_one_new_exact_selected_identity()
    {
        const string Status = "Authority ready. Started · exact run run-2 is open in Runs.";

        Assert.Equal("run-2", GovernedVisibleRunReadiness.RequireNewSelectedRunId(Status, ["run-1"], ["run-2", "run-1"], ["run-2"]));
        GovernedVisibleRunReadiness.RequireUnambiguousBaseline(["run-1"]);
        Assert.Throws<InvalidOperationException>(() => GovernedVisibleRunReadiness.RequireUnambiguousBaseline(["run-1", "run-1"]));
        Assert.Throws<InvalidOperationException>(() => GovernedVisibleRunReadiness.RequireNewSelectedRunId(Status, ["run-2"], ["run-2"], ["run-2"]));
        Assert.Throws<InvalidOperationException>(() => GovernedVisibleRunReadiness.RequireNewSelectedRunId(Status, ["run-1"], ["run-2", "run-2"], ["run-2"]));
        Assert.Throws<InvalidOperationException>(() => GovernedVisibleRunReadiness.RequireNewSelectedRunId(Status, ["run-1"], ["run-2", "run-1"], ["run-1"]));
        Assert.Throws<InvalidOperationException>(() => GovernedVisibleRunReadiness.RequireNewSelectedRunId("Authority ready. Invocation unavailable: timed out", ["run-1"], ["run-1"], ["run-1"]));
        Assert.Throws<InvalidOperationException>(() => GovernedVisibleRunReadiness.RequireNewSelectedRunId("Authority ready. Started · exact run ../run is open in Runs.", ["run-1"], ["../run", "run-1"], ["../run"]));
    }

    [Fact]
    public async Task Restart_request_tracking_finishes_a_started_failure_before_begin_can_capture_it()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        var tracker = new ExpectedServerRestartRequestTracker(TargetAuthority);
        using var failureStarted = new ManualResetEventSlim();
        using var releaseFailure = new ManualResetEventSlim();
        var failureTask = Task.Run(() => tracker.ExecuteAtomicallyForTest(() =>
        {
            tracker.Track("request-1", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50");
            failureStarted.Set();
            Assert.True(releaseFailure.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(tracker.ProcessLoadingFailed("request-1", canceled: false, "net::ERR_CONNECTION_RESET"));
        }));

        Assert.True(failureStarted.Wait(TimeSpan.FromSeconds(5)));
        using var beginStarted = new ManualResetEventSlim();
        var beginTask = Task.Run(() =>
        {
            beginStarted.Set();
            tracker.BeginExpectedServerRestart();
        });
        Assert.True(beginStarted.Wait(TimeSpan.FromSeconds(5)));
        releaseFailure.Set();
        await failureTask;
        await beginTask;

        var context = tracker.ReadLogContext("request-1");
        Assert.False(context.BeganDuringOutage);
        Assert.False(context.CapturedAtRestart);
        Assert.False(tracker.ProcessLoadingFailed("request-1", canceled: false, "net::ERR_CONNECTION_RESET"));
    }

    [Fact]
    public void Restart_request_tracking_removes_canceled_captured_requests_before_later_diagnostics()
    {
        var tracker = new ExpectedServerRestartRequestTracker("127.0.0.1:5001");
        tracker.Track("request-1", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50");
        tracker.BeginExpectedServerRestart();

        Assert.True(tracker.ProcessLoadingFailed("request-1", canceled: true, "net::ERR_CONNECTION_RESET"));
        var context = tracker.ReadLogContext("request-1");
        Assert.False(context.CapturedAtRestart);
        Assert.False(tracker.ProcessLoadingFailed("request-1", canceled: false, "net::ERR_CONNECTION_RESET"));
    }

    [Fact]
    public void Restart_request_tracking_correlates_loading_failed_before_late_log_without_widening_diagnostics()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        const string RequestUrl = "https://127.0.0.1:5001/api/loop-runs?maximumCount=50";
        var tracker = new ExpectedServerRestartRequestTracker(TargetAuthority);
        tracker.Track("captured", RequestUrl);
        tracker.BeginExpectedServerRestart();

        Assert.True(tracker.ProcessLoadingFailed("captured", canceled: false, "net::ERR_CONNECTION_RESET"));
        Assert.False(tracker.IsExpectedServerRestartLogEntry("captured", "network", "fetch failed", null));
        tracker.EndExpectedServerRestart();
        Assert.True(tracker.IsExpectedServerRestartLogEntry("captured", "network", "fetch failed: net::ERR_CONNECTION_RESET", null));
        var context = tracker.ReadLogContext("captured");
        Assert.False(context.CapturedAtRestart);
        Assert.False(tracker.IsExpectedServerRestartLogEntry("captured", "network", "fetch failed: net::ERR_CONNECTION_RESET", null));

        tracker.Track("non-reset", RequestUrl);
        tracker.BeginExpectedServerRestart();
        Assert.False(tracker.ProcessLoadingFailed("non-reset", canceled: false, "fetch failed"));
        Assert.False(tracker.IsExpectedServerRestartLogEntry("non-reset", "network", "fetch failed", null));

        tracker.Track("http-error", RequestUrl);
        tracker.BeginExpectedServerRestart();
        Assert.False(tracker.ProcessLoadingFailed("http-error", canceled: false, "500 (Internal Server Error)"));
        Assert.False(tracker.IsExpectedServerRestartLogEntry("http-error", "network", "500 (Internal Server Error)", null));

        tracker.Track("external", "https://example.test/api/loop-runs?maximumCount=50");
        Assert.False(tracker.IsExpectedServerRestartLogEntry("external", "network", "fetch failed: net::ERR_CONNECTION_RESET", "https://example.test/api/loop-runs?maximumCount=50"));
    }

    [Fact]
    public void Restart_request_tracking_correlates_log_before_loading_failed_and_cleans_after_both_channels()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        const string RequestUrl = "https://127.0.0.1:5001/api/session";
        var tracker = new ExpectedServerRestartRequestTracker(TargetAuthority);
        tracker.Track("request-1", RequestUrl);
        tracker.BeginExpectedServerRestart();

        Assert.True(tracker.IsExpectedServerRestartLogEntry("request-1", "network", "fetch failed: net::ERR_CONNECTION_RESET", RequestUrl));
        Assert.True(tracker.ProcessLoadingFailed("request-1", canceled: false, "net::ERR_CONNECTION_RESET"));
        var context = tracker.ReadLogContext("request-1");
        Assert.False(context.BeganDuringOutage);
        Assert.False(context.CapturedAtRestart);
    }

    [Fact]
    public void Restart_request_tracking_freezes_only_requests_drained_before_the_barrier()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        const string RequestUrl = "https://127.0.0.1:5001/api/loop-runs?maximumCount=50";
        var tracker = new ExpectedServerRestartRequestTracker(TargetAuthority);
        tracker.PrepareExpectedServerRestart();
        tracker.Track("queued-before-barrier", RequestUrl);
        tracker.FreezeExpectedServerRestart();

        Assert.True(tracker.ProcessLoadingFailed("queued-before-barrier", canceled: false, "net::ERR_CONNECTION_RESET"));

        tracker.Track("started-after-barrier", RequestUrl);
        Assert.False(tracker.ProcessLoadingFailed("started-after-barrier", canceled: false, "net::ERR_CONNECTION_RESET"));
    }

    [Fact]
    public void Restart_request_tracking_aborts_preparing_state_fail_closed()
    {
        var tracker = new ExpectedServerRestartRequestTracker("127.0.0.1:5001");
        tracker.PrepareExpectedServerRestart();
        tracker.Track("request-1", "https://127.0.0.1:5001/api/loop-runs?maximumCount=50");
        tracker.AbortExpectedServerRestart();

        Assert.False(tracker.IsExpectedServerRestart());
        Assert.False(tracker.ProcessLoadingFailed("request-1", canceled: false, "net::ERR_CONNECTION_RESET"));
    }

    [Fact]
    public async Task Restart_receive_barrier_freezes_before_await_continuation_can_track_new_request()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        const string RequestUrl = "https://127.0.0.1:5001/api/loop-runs?maximumCount=50";
        var tracker = new ExpectedServerRestartRequestTracker(TargetAuthority);
        var responseHandlers = new PendingBrowserCommandResponses();
        tracker.PrepareExpectedServerRestart();
        using var responseEntered = new ManualResetEventSlim();
        using var releaseResponse = new ManualResetEventSlim();
        responseHandlers.Add(1, _ => tracker.ExecuteAtomicallyForTest(() =>
        {
            responseEntered.Set();
            Assert.True(releaseResponse.Wait(TimeSpan.FromSeconds(5)));
            tracker.FreezeExpectedServerRestart();
        }));

        using var response = JsonDocument.Parse("{}");
        var responseTask = Task.Run(() => responseHandlers.Handle(1, response.RootElement));
        Assert.True(responseEntered.Wait(TimeSpan.FromSeconds(5)));
        var continuationTask = Task.Run(() => tracker.Track("post-barrier", RequestUrl));
        releaseResponse.Set();
        await responseTask;
        await continuationTask;

        Assert.False(tracker.ProcessLoadingFailed("post-barrier", canceled: false, "net::ERR_CONNECTION_RESET"));
    }

    [Fact]
    public void Restart_request_tracking_clears_late_diagnostic_correlation_when_redirect_leaves_authority()
    {
        const string TargetAuthority = "127.0.0.1:5001";
        const string TargetUrl = "https://127.0.0.1:5001/api/loop-runs?maximumCount=50";
        const string ExternalUrl = "https://example.test/api/loop-runs?maximumCount=50";
        var tracker = new ExpectedServerRestartRequestTracker(TargetAuthority);
        tracker.Track("redirected", TargetUrl);
        tracker.BeginExpectedServerRestart();
        Assert.True(tracker.IsExpectedServerRestartLogEntry("redirected", "network", "fetch failed: net::ERR_CONNECTION_RESET", TargetUrl));

        tracker.Track("redirected", ExternalUrl);

        Assert.False(tracker.ProcessLoadingFailed("redirected", canceled: false, "net::ERR_CONNECTION_RESET"));
        Assert.False(tracker.IsExpectedServerRestartLogEntry("redirected", "network", "fetch failed: net::ERR_CONNECTION_RESET", ExternalUrl));
    }

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
            await browser.BeginExpectedServerRestartAsync();
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
            await browser.EndExpectedServerRestartAsync();
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
        var codexExecutable = await FakeCodexExecutable.CreateBrowserApprovalAsync(workspace);
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

            await browser.BeginExpectedServerRestartAsync();
            await app.DisposeAsync();
            retiredServerOutput = app.FormatOutput();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test");
            await browser.ReloadAsync(acceptBeforeUnload: true);
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
            await browser.EndExpectedServerRestartAsync();
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

            var providerInstancesBeforeCustomRuns = await ReadFakeCodexProcessInstancesAsync(workspace);
            const string ApprovalPrompt = "browser-approval-unavailable";

            await InvokeLoopAsync(browser, ApprovalPrompt);
            var approvalProviderInstance = await WaitForFakeCodexEventAsync(workspace, ApprovalPrompt, "tool-call", providerInstancesBeforeCustomRuns);
            await WaitForFakeCodexEventAsync(workspace, ApprovalPrompt, "tool-response", providerInstancesBeforeCustomRuns, approvalProviderInstance, approved: false);
            await WaitForFakeCodexEventAsync(workspace, ApprovalPrompt, "turn-completed", providerInstancesBeforeCustomRuns, approvalProviderInstance, approved: false);
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '1' && document.getElementById('runSubtitle').textContent.includes('· Completed') && document.getElementById('runTimeline').textContent.includes('canonical_governed_loop_approval_unavailable')");
            await AssertOnlyExpectedFreshFakeCodexAttemptAsync(workspace, providerInstancesBeforeCustomRuns, approvalProviderInstance);
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('loopApprovalPanel').hidden && document.querySelectorAll('#loopApprovals button').length === 0"));
            var approvalTimeline = await browser.EvaluateStringAsync("document.getElementById('runTimeline').textContent");
            Assert.Contains("browser governed tool rejected", approvalTimeline, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("canonical_governed_loop_approval_unavailable", approvalTimeline, StringComparison.Ordinal);
            Assert.DoesNotContain("approved browser evidence", approvalTimeline, StringComparison.Ordinal);
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
            var providerInstancesBeforeFailure = await ReadFakeCodexProcessInstancesAsync(workspace);
            await InvokeLoopAsync(browser, "browser-provider-failure");
            var failureProviderInstance = await WaitForFakeCodexEventAsync(workspace, "browser-provider-failure", "turn-failed", providerInstancesBeforeFailure);
            await browser.WaitForExpressionAsync("document.getElementById('runCount').textContent === '2' && document.getElementById('runSubtitle').textContent.includes('· Failed') && document.getElementById('runTimeline').textContent.includes('Provider attempt failed without an automatic retry')");
            await AssertOnlyExpectedFreshFakeCodexAttemptAsync(workspace, providerInstancesBeforeFailure, failureProviderInstance);
            Assert.Contains("Failed", await browser.EvaluateStringAsync("document.getElementById('runSubtitle').textContent"), StringComparison.Ordinal);
            Assert.False(await browser.EvaluateBooleanAsync("document.getElementById('runTimeline').textContent.includes('Needs Review')"));
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
        const string BrowserProfileId = "org.example/model-profile/browser-schedule";
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
        var browserProfile = new BrowserModelProfileSpec(
            BrowserProfileId,
            "browser-schedule",
            "Test-only exact bounded browser model profile.",
            "gpt-test",
            true);
        var browserProfileDescriptor = BrowserProfileWebHost.CreateDescriptor(browserProfile);
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [browserProfileDescriptor]);
        var paths = new WorkspacePaths(workspace.RootPath);
        var authoringRole = await CreateScheduleGraphAuthoringRoleAsync(paths, [browserProfileDescriptor.Id.Value]);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(
            workspace.RootPath,
            GetFreePort(),
            codexExecutable,
            "gpt-test",
            capabilityTrustRoot,
            [browserProfile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphCatalog').textContent.includes('schedule-trigger') && document.getElementById('governedGraphCatalog').textContent.includes('provider-inference') && document.getElementById('governedGraphCatalog').textContent.includes('success-exit') && document.getElementById('governedGraphCatalog').textContent.includes('fail-terminal')");
            await browser.WaitForExpressionAsync($"document.getElementById('governedGraphModelProfile').textContent.includes('gpt-test') && [...document.getElementById('governedGraphModelProfile').options].some((option) => option.value === '{BrowserProfileId}' && !option.disabled)");
            Assert.True(await browser.EvaluateBooleanAsync($"[...document.querySelectorAll('#governedGraphModelProfile option')].some((option) => option.value === '{BuiltInCapabilityCatalog.CodexModelProfileCapabilityId}' && option.disabled && option.textContent.toLowerCase().includes('adapterunavailable'))"));

            await SetValueAsync(
                browser,
                "#governedGraphRole",
                $"{authoringRole.Identity.RoleId}:{authoringRole.Identity.Revision}:{authoringRole.ContentHash}",
                "change");
            await SetValueAsync(browser, "#governedGraphModelRoutingMode", "exact", "change");
            await SetValueAsync(browser, "#governedGraphId", "browser-scheduled-graph");
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphLoadButton').disabled");
            await ClickAsync(browser, "#governedGraphLoadButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphNotice').textContent.includes('No durable governed graph has this ID') && !document.getElementById('governedGraphNewButton').disabled");
            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-1");
            await SetValueAsync(browser, "#governedGraphDisplayName", "Browser scheduled graph");
            await SetValueAsync(browser, "#governedGraphPurpose", "Publish one server-cataloged scheduled graph.");
            await ClickAsync(browser, "#governedGraphNewButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Local draft')");
            await SetValueAsync(browser, "#governedGraphModelProfile", BrowserProfileId, "change");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "schedule-trigger");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "provider-inference");
            await SetValueAsync(browser, "#governedGraphInspector input:not([type='number'])", "Return the exact scheduled request.");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Model profile evidence') && !document.getElementById('governedGraphInspector').textContent.includes('Loading')");
            var modelInspector = await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent");
            Assert.Contains("Eligible", modelInspector, StringComparison.Ordinal);
            Assert.Contains("runtime admission still required", modelInspector, StringComparison.Ordinal);
            Assert.Contains(BrowserProfileId, modelInspector, StringComparison.Ordinal);
            Assert.Contains("sensitive", modelInspector, StringComparison.Ordinal);
            Assert.Contains("remote", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("attempt input unbounded", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("node input unbounded", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("run input unbounded", modelInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Input Tokens Authoritative and hard bounded at dispatch", modelInspector, StringComparison.Ordinal);
            Assert.Contains("Monetary Cost Unavailable", modelInspector, StringComparison.Ordinal);
            Assert.Contains("Ordered model fallback candidatesNone", modelInspector, StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphInspector button", "Preview and enable retry");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('retry-provider-inference') && document.getElementById('governedGraphInspector').textContent.includes('3 total attempts') && document.getElementById('governedGraphInspector').textContent.includes('runtime admission still required')");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "wait-timestamp");
            var waitDeadline = DateTime.UtcNow.AddDays(1).ToString("O", CultureInfo.InvariantCulture);
            await SetValueAsync(browser, "#governedGraphInspector input:not([type='number'])", waitDeadline);
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Wait configuration') && document.getElementById('governedGraphInspector').textContent.includes('server validates it on save or publish')");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "wait-authenticated-event");
            const string WaitEventReference = "browser-authenticated-event-reference";
            await SetValueAsync(browser, "#governedGraphInspector input:not([type='number'])", WaitEventReference);
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Authenticated event') && document.getElementById('governedGraphInspector').textContent.includes('server validates it on save or publish')");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "fail-terminal");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "success-exit");

            await AddGovernedGraphControlAsync(browser, "schedule-trigger", "provider-inference", "Always");
            await AddGovernedGraphBindingAsync(browser, "schedule-trigger", "provider-inference", "Data · request → request");
            await AddGovernedGraphBindingAsync(browser, "schedule-trigger", "provider-inference", "Context · invocation-context → invocation-context");
            await AddGovernedGraphControlAsync(browser, "provider-inference", "wait-timestamp", "Success");
            await AddGovernedGraphControlAsync(browser, "provider-inference", "fail-terminal", "Failure");
            await AddGovernedGraphControlAsync(browser, "wait-timestamp", "wait-authenticated-event", "Success");
            await AddGovernedGraphControlAsync(browser, "wait-authenticated-event", "success-exit", "Success");
            await AddGovernedGraphBindingAsync(browser, "provider-inference", "success-exit", "Data · result → result");

            await browser.WaitForExpressionAsync($"!document.getElementById('governedGraphSaveButton').disabled && document.getElementById('governedGraphModelProfile').value === '{BrowserProfileId}'");
            await ClickAsync(browser, "#governedGraphSaveButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Draft') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphPublishButton').disabled");
            await ClickAsync(browser, "#governedGraphPublishButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            Assert.Contains("schedule-trigger", await browser.EvaluateStringAsync("document.getElementById('governedGraphCanvas').textContent"), StringComparison.Ordinal);
            Assert.Contains("wait-timestamp", await browser.EvaluateStringAsync("document.getElementById('governedGraphCanvas').textContent"), StringComparison.Ordinal);
            Assert.Contains("wait-authenticated-event", await browser.EvaluateStringAsync("document.getElementById('governedGraphCanvas').textContent"), StringComparison.Ordinal);
            Assert.Contains("fail-terminal", await browser.EvaluateStringAsync("document.getElementById('governedGraphCanvas').textContent"), StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "schedule-trigger");
            Assert.Contains("org.embodysense/triggers/time", await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent"), StringComparison.Ordinal);
            var firstLocalOccurrence = DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);
            await SetValueAsync(browser, "#governedScheduleRecurrenceKind", "fixed-interval", "change");
            await SetValueAsync(browser, "#governedScheduleFirstLocalOccurrence", firstLocalOccurrence);
            await SetValueAsync(browser, "#governedScheduleFixedIntervalSeconds", "300");
            await SetValueAsync(browser, "#governedScheduleTimeZoneId", "UTC");
            await browser.WaitForExpressionAsync("!document.getElementById('governedScheduleSubmitButton').disabled");
            await ClickAsync(browser, "#governedScheduleSubmitButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('server derived least-authority terms') && !document.getElementById('governedScheduleSubmitButton').disabled");
            await ClickAsync(browser, "#governedScheduleSubmitButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('immutable canonical schedule was created')");
            var scheduleId = await browser.EvaluateStringAsync("document.getElementById('governedScheduleInspectId').value");
            Assert.Matches("^schedule-[a-f0-9]{48}$", scheduleId);
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).concat(Object.keys(sessionStorage)).some((key) => key.toLowerCase().includes('schedule-author'))"));

            var firstScheduleReloadGeneration = Guid.NewGuid().ToString("N");
            await browser.EvaluateAsync($"window.__scheduleReloadGeneration = '{firstScheduleReloadGeneration}'");
            await browser.WaitForExpressionAsync($"window.__scheduleReloadGeneration === '{firstScheduleReloadGeneration}'");
            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync($"typeof window.__scheduleReloadGeneration === 'undefined' || window.__scheduleReloadGeneration !== '{firstScheduleReloadGeneration}'");
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && document.getElementById('governedGraphTab').getAttribute('aria-disabled') === 'false' && !document.getElementById('governedScheduleSubmitButton').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphTab').getAttribute('aria-disabled') === 'false' && !document.getElementById('governedScheduleSubmitButton').disabled && document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.querySelectorAll('#governedGraphCanvas .governed-graph-node').length === 6 && document.getElementById('governedGraphCanvas').textContent.includes('fail-terminal')");
            Assert.Equal("browser-scheduled-graph", await browser.EvaluateStringAsync("document.getElementById('governedGraphId').value"));
            Assert.Equal(BrowserProfileId, await browser.EvaluateStringAsync("document.getElementById('governedGraphModelProfile').value"));
            Assert.Equal("exact", await browser.EvaluateStringAsync("document.getElementById('governedGraphModelRoutingMode').value"));
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "provider-inference");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Eligible') && document.getElementById('governedGraphInspector').textContent.includes('exact selector') && document.getElementById('governedGraphInspector').textContent.includes('retry-provider-inference') && document.getElementById('governedGraphInspector').textContent.includes('3 total attempts')");
            Assert.Contains(BrowserProfileId, await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent"), StringComparison.Ordinal);
            Assert.Contains("1 immutable revision artifact", await browser.EvaluateStringAsync("document.getElementById('governedGraphLifecycle').textContent"), StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "wait-timestamp");
            Assert.Equal(waitDeadline, await browser.EvaluateStringAsync("document.querySelector(\"#governedGraphInspector input:not([type='number'])\").value"));
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "wait-authenticated-event");
            Assert.Equal(WaitEventReference, await browser.EvaluateStringAsync("document.querySelector(\"#governedGraphInspector input:not([type='number'])\").value"));
            await SetValueAsync(browser, "#governedScheduleInspectId", scheduleId);
            await ClickAsync(browser, "#governedScheduleInspectButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('Inspected') && !document.getElementById('governedSchedulePrepareEditButton').disabled");
            Assert.Contains(scheduleId, await browser.EvaluateStringAsync("document.getElementById('governedScheduleResult').textContent"), StringComparison.Ordinal);
            await ClickAsync(browser, "#governedSchedulePrepareEditButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('Successor prepared') && document.getElementById('governedScheduleEnabled').checked === false");
            await SetValueAsync(browser, "#governedScheduleFixedIntervalSeconds", "600", "input");
            await ClickAsync(browser, "#governedScheduleSubmitButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('Replacement complete') && document.getElementById('governedScheduleInspectId').value !== '' && !document.getElementById('governedScheduleInspectButton').disabled");
            var successorScheduleId = await browser.EvaluateStringAsync("document.getElementById('governedScheduleInspectId').value");
            Assert.Matches("^schedule-[a-f0-9]{48}$", successorScheduleId);
            Assert.NotEqual(scheduleId, successorScheduleId);
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).concat(Object.keys(sessionStorage)).some((key) => key.toLowerCase().includes('schedule-author'))"));

            await SetValueAsync(browser, "#governedScheduleInspectId", scheduleId);
            await ClickAsync(browser, "#governedScheduleInspectButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('Inspected') && document.getElementById('governedScheduleResult').textContent.includes('state revision 2') && !document.getElementById('governedScheduleInspectButton').disabled");
            Assert.Contains("disabled", await browser.EvaluateStringAsync("document.getElementById('governedScheduleResult').textContent"), StringComparison.OrdinalIgnoreCase);
            await SetValueAsync(browser, "#governedScheduleInspectId", successorScheduleId);
            await ClickAsync(browser, "#governedScheduleInspectButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('Inspected') && document.getElementById('governedScheduleResult').textContent.includes('state revision 2') && !document.getElementById('governedScheduleInspectButton').disabled");
            Assert.Contains("enabled", await browser.EvaluateStringAsync("document.getElementById('governedScheduleResult').textContent"), StringComparison.OrdinalIgnoreCase);
            var secondScheduleReloadGeneration = Guid.NewGuid().ToString("N");
            await browser.EvaluateAsync($"window.__scheduleReloadGeneration = '{secondScheduleReloadGeneration}'");
            await browser.WaitForExpressionAsync($"window.__scheduleReloadGeneration === '{secondScheduleReloadGeneration}'");
            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync($"typeof window.__scheduleReloadGeneration === 'undefined' || window.__scheduleReloadGeneration !== '{secondScheduleReloadGeneration}'");
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('governedGraphTab').getAttribute('aria-disabled') === 'false'");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("!document.getElementById('governedScheduleInspectButton').disabled");
            await SetValueAsync(browser, "#governedScheduleInspectId", successorScheduleId);
            await ClickAsync(browser, "#governedScheduleInspectButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedScheduleResult').textContent.includes('Inspected') && document.getElementById('governedScheduleResult').textContent.includes('state revision 2') && !document.getElementById('governedScheduleInspectButton').disabled");
            Assert.Contains("enabled", await browser.EvaluateStringAsync("document.getElementById('governedScheduleResult').textContent"), StringComparison.OrdinalIgnoreCase);
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).concat(Object.keys(sessionStorage)).some((key) => key.toLowerCase().includes('schedule-author'))"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync(("/api/governed-graphs/detail?graphId=browser-scheduled-graph", 404));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_publishes_and_reloads_a_server_cataloged_schedule_graph), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Browser_authors_publishes_and_reloads_an_exact_governed_command_action()
    {
        const string BrowserProfileId = "org.example/model-profile/browser-command";
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var capabilityTrustRoot = Path.Combine(workspace.ServerStatePath, "browser-command-capability-catalog");
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var commandAction = await CreateBrowserCommandActionRegistrationAsync();
        var registration = commandAction.Registration;
        await InstallBrowserCommandActionAsync(paths, capabilityTrustRoot, registration);
        var browserProfile = new BrowserModelProfileSpec(
            BrowserProfileId,
            "browser-command",
            "Test-only exact bounded browser command model profile.",
            "gpt-test",
            true);
        var browserProfileDescriptor = BrowserProfileWebHost.CreateDescriptor(browserProfile);
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [browserProfileDescriptor]);
        var authoringRole = await CreateScheduleGraphAuthoringRoleAsync(paths, [registration.Template.Capability.Id.Value, browserProfileDescriptor.Id.Value]);
        var commandNodeId = CommandActionNodeDescriptors.For(registration.Template).TypeId;
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(
            workspace.RootPath,
            GetFreePort(),
            codexExecutable,
            "gpt-test",
            capabilityTrustRoot,
            [browserProfile],
            [commandAction.Spec]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphCatalog').textContent.includes('Command Action · command/browser-json-echo v1')");
            Assert.True(await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#governedGraphCatalog button')].some((button) => button.textContent.includes('command/browser-json-echo') && !button.disabled)"));

            await SetValueAsync(browser, "#governedGraphRole", $"{authoringRole.Identity.RoleId}:{authoringRole.Identity.Revision}:{authoringRole.ContentHash}", "change");
            await SetValueAsync(browser, "#governedGraphId", "browser-command-graph");
            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-1");
            await SetValueAsync(browser, "#governedGraphDisplayName", "Browser command graph");
            await SetValueAsync(browser, "#governedGraphPurpose", "Execute one exact server-registered command Action.");
            await SetValueAsync(browser, "#governedGraphModelRoutingMode", "exact", "change");
            await SetValueAsync(browser, "#governedGraphModelProfile", BrowserProfileId, "change");
            await ClickAsync(browser, "#governedGraphNewButton");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "manual-trigger");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "Command Action · command/browser-json-echo v1");
            await SetValueAsync(browser, "#governedGraphInspector input:not([type='number'])", "{\"status\":\"ok\"}");
            var commandInspector = await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent");
            Assert.Contains(registration.Template.ContentHash, commandInspector, StringComparison.Ordinal);
            Assert.Contains("Credentials not required", commandInspector, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Denied network", commandInspector, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(paths.CapabilityArtifactsPath, commandInspector, StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "success-exit");

            await AddGovernedGraphControlAsync(browser, "manual-trigger", commandNodeId, "Always");
            await AddGovernedGraphControlAsync(browser, commandNodeId, "success-exit", "Success");
            await AddGovernedGraphBindingAsync(browser, commandNodeId, "success-exit", "Data · result → result");

            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphSaveButton').disabled");
            await ClickAsync(browser, "#governedGraphSaveButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Draft') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            await ClickAsync(browser, "#governedGraphPublishButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");

            await browser.ReloadAsync();
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.querySelectorAll('#governedGraphCanvas .governed-graph-node').length === 3");
            Assert.Contains("command-", await browser.EvaluateStringAsync("document.getElementById('governedGraphCanvas').textContent"), StringComparison.Ordinal);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_publishes_and_reloads_an_exact_governed_command_action), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Browser_authors_publishes_invokes_and_inspects_a_bounded_visible_governed_cycle()
    {
        const string BrowserProfileId = "org.example/model-profile/browser-visible-cycle";
        using var workspace = new TestWorkspace();
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var capabilityTrustRoot = Path.Combine(workspace.ServerStatePath, "browser-visible-cycle-capability-catalog");
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var browserProfile = new BrowserModelProfileSpec(
            BrowserProfileId,
            "browser-visible-cycle",
            "Test-only exact bounded browser governed-cycle model profile.",
            "gpt-test",
            true);
        var browserProfileDescriptor = BrowserProfileWebHost.CreateDescriptor(browserProfile);
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [browserProfileDescriptor]);
        var authoringRole = await CreateScheduleGraphAuthoringRoleAsync(paths, [browserProfileDescriptor.Id.Value]);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(
            workspace.RootPath,
            GetFreePort(),
            codexExecutable,
            "gpt-test",
            capabilityTrustRoot,
            [browserProfile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await ClickAsync(browser, "#loopsNav");
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphCatalog').textContent.includes('manual-trigger') && document.getElementById('governedGraphCatalog').textContent.includes('schema-conformance') && document.getElementById('governedGraphCatalog').textContent.includes('exact-text-condition') && document.getElementById('governedGraphCatalog').textContent.includes('fail-terminal')");
            await browser.WaitForExpressionAsync($"document.getElementById('governedGraphModelProfile').textContent.includes('gpt-test') && [...document.getElementById('governedGraphModelProfile').options].some((option) => option.value === '{BrowserProfileId}' && !option.disabled)");
            await SetValueAsync(browser, "#governedGraphRole", $"{authoringRole.Identity.RoleId}:{authoringRole.Identity.Revision}:{authoringRole.ContentHash}", "change");
            await SetValueAsync(browser, "#governedGraphModelRoutingMode", "exact", "change");
            await SetValueAsync(browser, "#governedGraphId", "browser-visible-cycle-graph");
            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-1");
            await SetValueAsync(browser, "#governedGraphDisplayName", "Browser visible bounded cycle graph");
            await SetValueAsync(browser, "#governedGraphPurpose", "Prove one visible success and one bounded explicit failure through the public browser boundary.");
            await ClickAsync(browser, "#governedGraphNewButton");
            await SetValueAsync(browser, "#governedGraphModelProfile", BrowserProfileId, "change");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "manual-trigger");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "provider-inference");
            await SetGovernedGraphInspectorParameterAsync(browser, "instruction", "Return retry or terminal only. visible-cycle-marker");
            await SetGovernedGraphInspectorParameterAsync(browser, "max-iterations", "3");
            await SetGovernedGraphInspectorParameterAsync(browser, "max-duration-milliseconds", "120000");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('Model profile evidence') && !document.getElementById('governedGraphInspector').textContent.includes('Loading')");
            Assert.Contains("Eligible", await browser.EvaluateStringAsync("document.getElementById('governedGraphInspector').textContent"), StringComparison.Ordinal);
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "schema-conformance");
            await SetGovernedGraphInspectorParameterAsync(browser, "max-iterations", "3");
            await SetGovernedGraphInspectorParameterAsync(browser, "max-duration-milliseconds", "120000");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "model-decision-condition");
            await SetGovernedGraphInspectorParameterAsync(browser, "true-decision", "retry");
            await SetGovernedGraphInspectorParameterAsync(browser, "false-decision", "terminal");
            await SetGovernedGraphInspectorParameterAsync(browser, "max-iterations", "3");
            await SetGovernedGraphInspectorParameterAsync(browser, "max-duration-milliseconds", "120000");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "exact-text-condition");
            await SetGovernedGraphInspectorParameterAsync(browser, "expected", "visible-cycle-success");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "fail-terminal");
            await SetGovernedGraphInspectorParameterAsync(browser, "code", "visible-cycle-exhausted");
            await SetGovernedGraphInspectorParameterAsync(browser, "explanation", "The bounded visible cycle reached its terminal non-success decision.");
            await ClickButtonByTextAsync(browser, "#governedGraphCatalog button", "success-exit");
            await AddGovernedGraphControlAsync(browser, "manual-trigger", "provider-inference", "Always");
            await AddGovernedGraphControlAsync(browser, "provider-inference", "schema-conformance", "Success");
            await AddGovernedGraphControlAsync(browser, "schema-conformance", "model-decision-condition", "Success");
            await AddGovernedGraphControlAsync(browser, "model-decision-condition", "provider-inference", "True");
            await AddGovernedGraphControlAsync(browser, "model-decision-condition", "exact-text-condition", "False");
            await AddGovernedGraphControlAsync(browser, "exact-text-condition", "success-exit", "True");
            await AddGovernedGraphControlAsync(browser, "exact-text-condition", "fail-terminal", "False");
            await AddGovernedGraphBindingAsync(browser, "manual-trigger", "provider-inference", "Data · request → request");
            await AddGovernedGraphBindingAsync(browser, "manual-trigger", "provider-inference", "Context · invocation-context → invocation-context");
            await AddGovernedGraphBindingAsync(browser, "provider-inference", "schema-conformance", "Data · result → input");
            await AddGovernedGraphBindingAsync(browser, "provider-inference", "model-decision-condition", "Data · result → decision");
            await AddGovernedGraphBindingAsync(browser, "provider-inference", "success-exit", "Data · result → result");
            await AddGovernedGraphBindingAsync(browser, "manual-trigger", "exact-text-condition", "Data · request → value");
            await browser.WaitForExpressionAsync($"!document.getElementById('governedGraphSaveButton').disabled && document.getElementById('governedGraphModelProfile').value === '{BrowserProfileId}'");
            await ClickAsync(browser, "#governedGraphSaveButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Draft') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");
            await ClickAsync(browser, "#governedGraphPublishButton");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.getElementById('governedGraphNotice').textContent.includes('Committed')");

            const string SuccessPrompt = "visible-cycle-success";
            const string ExhaustionPrompt = "visible-cycle-exhaustion";
            var successRunId = await InvokePublishedGraphThroughVisibleControlsAsync(browser, SuccessPrompt);
            using var successRun = JsonDocument.Parse(await WaitForTerminalRunFromBrowserAsync(browser, successRunId));
            Assert.Equal(CustomLoopRunStatus.Completed.ToString(), successRun.RootElement.GetProperty("status").GetString());
            Assert.Equal(SuccessPrompt, successRun.RootElement.GetProperty("triggerPrompt").GetString());
            Assert.Equal(JsonValueKind.Null, successRun.RootElement.GetProperty("invokingConversation").ValueKind);

            var exhaustedRunId = await InvokePublishedGraphThroughVisibleControlsAsync(browser, ExhaustionPrompt);
            using var exhaustedRun = JsonDocument.Parse(await WaitForTerminalRunFromBrowserAsync(browser, exhaustedRunId));
            Assert.NotEqual(successRunId, exhaustedRunId);
            Assert.NotEqual(successRun.RootElement.GetProperty("admissionOperationId").GetString(), exhaustedRun.RootElement.GetProperty("admissionOperationId").GetString());
            Assert.NotEqual(successRun.RootElement.GetProperty("triggerPrompt").GetString(), exhaustedRun.RootElement.GetProperty("triggerPrompt").GetString());
            Assert.True(
                string.Equals(exhaustedRun.RootElement.GetProperty("status").GetString(), CustomLoopRunStatus.Failed.ToString(), StringComparison.Ordinal),
                exhaustedRun.RootElement.GetRawText());
            Assert.Equal("visible-cycle-exhausted", exhaustedRun.RootElement.GetProperty("failureCode").GetString());
            Assert.Contains(
                exhaustedRun.RootElement.GetProperty("frontier").GetProperty("nodes").EnumerateArray(),
                node => node.GetProperty("nodeId").GetString() == "provider-inference"
                    && node.GetProperty("visitOrdinal").GetInt32() == 3);
            Assert.DoesNotContain("token=", exhaustedRun.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Browser_authors_publishes_invokes_and_inspects_a_bounded_visible_governed_cycle), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Browser_preserves_server_owned_profile_fallback_order_override_conflicts_and_safe_text()
    {
        const string PrimaryProfileId = "org.example/model-profile/primary";
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
            new BrowserModelProfileSpec(PrimaryProfileId, "primary", "Primary browser model profile.", "gpt-primary", true),
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
            await browser.WaitForExpressionAsync("!document.getElementById('loopsView').hidden && document.getElementById('loopList').textContent.includes('System loop') && !document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphModelProfile').textContent.includes('gpt-secondary') && document.getElementById('governedGraphModelProfile').textContent.includes('gpt-tertiary') && document.getElementById('governedGraphModelProfile').textContent.includes('gpt-unavailable')");
            Assert.True(await browser.EvaluateBooleanAsync("[...document.querySelectorAll('#governedGraphModelProfile option')].some((option) => option.value === 'org.example/model-profile/unavailable' && option.disabled && option.textContent.toLowerCase().includes('unavailable'))"));
            Assert.False(await browser.EvaluateBooleanAsync("window.__modelProfileXss || Boolean(document.querySelector('[data-model-profile-xss]'))"));

            var authoringRoleValue = $"{authoringRole.Identity.RoleId}:{authoringRole.Identity.Revision}:{authoringRole.ContentHash}";
            await SetValueAsync(browser, "#governedGraphRole", authoringRoleValue, "change");
            await SetValueAsync(browser, "#governedGraphModelRoutingMode", "exact", "change");
            await SetValueAsync(browser, "#governedGraphModelProfile", PrimaryProfileId, "change");
            await SetValueAsync(browser, "#governedGraphId", "browser-profile-routing-graph");
            await SetValueAsync(browser, "#governedGraphRevisionId", "revision-1");
            await SetValueAsync(browser, "#governedGraphDisplayName", "Browser profile routing graph");
            await SetValueAsync(browser, "#governedGraphPurpose", "Preserve exact server-owned routing evidence.");
            await ClickAsync(browser, "#governedGraphNewButton");
            await browser.EvaluateAsync("(() => { const selected = new Set(['org.example/model-profile/secondary', 'org.example/model-profile/tertiary']); const control = document.getElementById('governedGraphFallbackProfiles'); for (const option of control.options) option.selected = selected.has(option.value); control.dispatchEvent(new Event('change', { bubbles: true })); })()");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphFallbackOrder').textContent.includes('1. org.example/model-profile/secondary') && document.getElementById('governedGraphFallbackOrder').textContent.includes('2. org.example/model-profile/tertiary')");
            await browser.EvaluateWithUserGestureAsync("document.querySelector('#governedGraphFallbackOrder li:first-child button:last-child').click()");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/tertiary') < document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/secondary')");

            var forgedStatus = await browser.EvaluateInt32Async($"(async () => {{ const catalog = await fetch('/api/governed-graphs/catalog').then((response) => response.json()); const profile = catalog.modelProfiles.profiles.find((item) => item.profileId === '{PrimaryProfileId}'); const response = await fetch('/api/model-profiles/preview', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify({{ policy: profile.recommendedExactPolicy, roleId: 'schedule-graph-author', nodeTypeId: 'provider-inference', authoredInputDataClasses: null, metadata: {{ modelId: 'forged-browser-model' }} }}) }}); return response.status; }})()");
            Assert.Equal(400, forgedStatus);
            Assert.Equal(PrimaryProfileId, await browser.EvaluateStringAsync("document.getElementById('governedGraphModelProfile').value"));

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
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphId').value === 'browser-profile-routing-graph' && !document.getElementById('governedGraphRefreshButton').disabled");
            await ClickAsync(browser, "#governedGraphRefreshButton");
            await browser.WaitForExpressionAsync($"document.getElementById('governedGraphLifecycle').textContent.includes('Published') && document.querySelectorAll('#governedGraphCanvas .governed-graph-node').length === 3 && document.getElementById('governedGraphRole').value === '{authoringRoleValue}' && document.getElementById('governedGraphModelProfile').value === '{PrimaryProfileId}'");
            Assert.Contains("org.example/model-profile/tertiary", await browser.EvaluateStringAsync("document.getElementById('governedGraphFallbackOrder').textContent"), StringComparison.Ordinal);
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/tertiary') < document.getElementById('governedGraphFallbackOrder').textContent.indexOf('org.example/model-profile/secondary')"));
            await ClickButtonByTextAsync(browser, "#governedGraphCanvas button", "provider-inference");
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInspector').textContent.includes('org.example/model-profile/secondary') && document.getElementById('governedGraphInspector').textContent.includes('org.example/model-profile/tertiary') && document.getElementById('governedGraphInspector').textContent.includes('Eligible')");
            Assert.Equal("Current tab owns this exact replacement.", await browser.EvaluateStringAsync("document.getElementById('governedGraphPurpose').value"));
            Assert.NotEqual("Stale browser replacement", await browser.EvaluateStringAsync("document.getElementById('governedGraphDisplayName').value"));
            Assert.False(await browser.EvaluateBooleanAsync("window.__modelProfileXss || Boolean(document.querySelector('[data-model-profile-xss]'))"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync(("/api/model-profiles/preview", 400));
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

    private static async Task<(CommandActionRegistration Registration, BrowserCommandActionSpec Spec)> CreateBrowserCommandActionRegistrationAsync()
    {
        var executable = await ReadBrowserCommandExecutableAsync();
        var spec = new BrowserCommandActionSpec(CapabilityIntegrityDigest.Compute(executable.Content).Value, executable.EntryPoint);
        return (BrowserProfileWebHost.CreateCommandActionRegistration(spec), spec);
    }

    private static async Task InstallBrowserCommandActionAsync(
        WorkspacePaths paths,
        string capabilityTrustRoot,
        CommandActionRegistration registration)
    {
        var catalogTrust = new FileCapabilityCatalogTrustProvider(capabilityTrustRoot);
        var catalog = new CapabilityCatalogService(new CapabilityCatalogStore(paths, catalogTrust));
        var revision = Assert.IsType<long>((await catalog.ReadAsync(null, 1)).Page?.CatalogRevision);
        revision = RequireApplied(await catalog.DeclareAsync(registration.Manifest.Descriptor, revision, "declare-browser-command"));
        revision = RequireApplied(await catalog.InstallAsync(registration.Manifest.Descriptor.Id, revision, "install-browser-command"));
        revision = RequireApplied(await catalog.VerifyAsync(registration.Manifest.Descriptor.Id, revision, "verify-browser-command"));
        revision = RequireApplied(await catalog.EnableAsync(registration.Manifest.Descriptor.Id, revision, "enable-browser-command"));
        _ = RequireApplied(await catalog.MarkHealthyAsync(registration.Manifest.Descriptor.Id, revision, "healthy-browser-command"));

        var artifactStore = new CapabilityArtifactStore(
            paths,
            new FileCapabilityArtifactStateTrustProvider(capabilityTrustRoot),
            BrowserCapabilityArtifactVerifier.Instance);
        var executable = await ReadBrowserCommandExecutableAsync();
        var stage = new CapabilityArtifactStageRequest(
            registration.Manifest,
            new CapabilityArtifactContent(executable.Content),
            new CapabilityArtifactTrustDecision(CapabilityArtifactTrustStatus.Verified, "browser-e2e-policy", "Verified."));
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifactStore.StageAsync(stage)).Status);
        if (!OperatingSystem.IsWindows())
        {
            var stagedExecutable = Path.Combine(
                paths.CapabilityArtifactsPath,
                "staged",
                registration.Manifest.Checksum.Value["sha256:".Length..],
                registration.Manifest.EntryPoint);
            File.SetUnixFileMode(stagedExecutable, File.GetUnixFileMode(stagedExecutable) | UnixFileMode.UserExecute);
        }
        Assert.Equal(CapabilityArtifactStoreStatus.Applied, (await artifactStore.ActivateAsync(new CapabilityArtifactActivationRequest(registration.Manifest, 0, "activate-browser-command"))).Status);

        static long RequireApplied(CapabilityCatalogMutationResult result)
        {
            Assert.Equal(CapabilityCatalogMutationStatus.Applied, result.Status);
            return Assert.IsType<long>(result.CatalogRevision);
        }
    }

    private static async Task<(byte[] Content, string EntryPoint)> ReadBrowserCommandExecutableAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "findstr.exe");
            return (await File.ReadAllBytesAsync(path), "findstr.exe");
        }

        return (Encoding.UTF8.GetBytes("#!/bin/sh\nIFS= read -r value\nprintf '%s' \"$value\"\n"), "browser-json-echo");
    }

    private static async Task<string> InvokePublishedGraphThroughVisibleControlsAsync(HeadlessBrowserSession browser, string prompt)
    {
        if (await browser.EvaluateBooleanAsync("document.getElementById('governedGraphView').hidden"))
        {
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphTab').disabled");
            await ClickAsync(browser, "#governedGraphTab");
            await browser.WaitForExpressionAsync("!document.getElementById('governedGraphView').hidden && document.getElementById('governedGraphLifecycle').textContent.includes('Published')");
        }
        await browser.WaitForExpressionAsync("!document.getElementById('governedGraphPrepareInvokeButton').disabled");
        await SetValueAsync(browser, "#governedGraphInvocationPrompt", prompt);
        await ClickAsync(browser, "#governedGraphPrepareInvokeButton");
        await browser.WaitForExpressionAsync("!document.getElementById('governedGraphConfirmInvokeButton').hidden && !document.getElementById('governedGraphConfirmInvokeButton').disabled");
        if (await browser.EvaluateBooleanAsync("document.getElementById('governedGraphConfirmInvokeButton').textContent.includes('Confirm authority')"))
        {
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphInvocationStatus').textContent.includes('Explicit confirmation')");
        }
        else
        {
            await browser.WaitForExpressionAsync("document.getElementById('governedGraphGrantChoices').textContent.includes('Eligible exact grant') && document.getElementById('governedGraphGrantSelection').options.length > 0");
        }
        var baselineRunIds = JsonSerializer.Deserialize<string[]>(await browser.EvaluateStringAsync("JSON.stringify([...document.querySelectorAll('#runList .run-id')].map((element) => element.textContent ?? ''))")) ?? [];
        GovernedVisibleRunReadiness.RequireUnambiguousBaseline(baselineRunIds);
        var selectedGraphId = await browser.EvaluateStringAsync("document.getElementById('governedGraphId').value");
        var selectedRevisionId = await browser.EvaluateStringAsync("document.getElementById('governedGraphRevisionId').value");
        var preConfirmStatus = await browser.EvaluateStringAsync("document.getElementById('governedGraphInvocationStatus').textContent");
        await browser.EvaluateAsync("""
            (() => {
              const probeKey = "__embodySenseGovernedInvocationProbe";
              const pendingKeyPrefix = "embodysense.governed-graph-pending-invocation.v1";
              if (globalThis[probeKey]) throw new Error("A governed invocation identity probe is already active.");
              const originalSetItem = Storage.prototype.setItem;
              const probe = { originalSetItem, capture: null, captureCount: 0 };
              globalThis[probeKey] = probe;
              Storage.prototype.setItem = function (key, value) {
                const result = originalSetItem.call(this, key, value);
                if (this === sessionStorage && typeof key === "string" && key.startsWith(`${pendingKeyPrefix}.`)) {
                  try {
                    const pending = JSON.parse(value);
                    probe.capture = {
                      captureCount: ++probe.captureCount,
                      valid:
                        pending?.schemaVersion === 1 &&
                        typeof pending?.workspaceScope === "string" &&
                        key === `${pendingKeyPrefix}.${pending.workspaceScope}` &&
                        typeof pending?.graphId === "string" &&
                        typeof pending?.revisionId === "string" &&
                        typeof pending?.operationId === "string",
                      graphId: typeof pending?.graphId === "string" ? pending.graphId : "",
                      revisionId: typeof pending?.revisionId === "string" ? pending.revisionId : "",
                      operationId: typeof pending?.operationId === "string" ? pending.operationId : "",
                    };
                  } catch {
                    probe.capture = { captureCount: ++probe.captureCount, valid: false, graphId: "", revisionId: "", operationId: "" };
                  }
                }
                return result;
              };
            })()
            """);
        string? capturedInvocationJson = null;
        try
        {
            await ClickAsync(browser, "#governedGraphConfirmInvokeButton");
            await browser.WaitForExpressionAsync("Boolean(globalThis.__embodySenseGovernedInvocationProbe?.capture)");
            capturedInvocationJson = await browser.EvaluateStringAsync("JSON.stringify(globalThis.__embodySenseGovernedInvocationProbe.capture)");
        }
        finally
        {
            try
            {
                await browser.EvaluateAsync("(() => { const probe = globalThis.__embodySenseGovernedInvocationProbe; if (!probe) return; Storage.prototype.setItem = probe.originalSetItem; delete globalThis.__embodySenseGovernedInvocationProbe; })()");
            }
            catch when (capturedInvocationJson is null)
            {
            }
        }

        using var capturedInvocation = JsonDocument.Parse(Assert.IsType<string>(capturedInvocationJson));
        Assert.Equal(1, capturedInvocation.RootElement.GetProperty("captureCount").GetInt32());
        Assert.True(capturedInvocation.RootElement.GetProperty("valid").GetBoolean(), "The visible invocation did not retain one scope-bound operation identity before dispatch.");
        var expectedGraphId = Assert.IsType<string>(capturedInvocation.RootElement.GetProperty("graphId").GetString());
        var expectedRevisionId = Assert.IsType<string>(capturedInvocation.RootElement.GetProperty("revisionId").GetString());
        var expectedOperationId = Assert.IsType<string>(capturedInvocation.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(selectedGraphId, expectedGraphId);
        Assert.Equal(selectedRevisionId, expectedRevisionId);
        Assert.False(string.IsNullOrWhiteSpace(expectedGraphId));
        Assert.False(string.IsNullOrWhiteSpace(expectedRevisionId));
        Assert.StartsWith("governed-invoke-", expectedOperationId, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(expectedOperationId["governed-invoke-".Length..], "D", out _), $"The retained governed invocation operation identity was malformed: {expectedOperationId}");
        var serializedPreConfirmStatus = JsonSerializer.Serialize(preConfirmStatus);
        try
        {
            await browser.WaitForExpressionAsync($"(() => {{ const status = document.getElementById('governedGraphInvocationStatus').textContent; return status.includes(' · exact run ') || (status !== {serializedPreConfirmStatus} && document.getElementById('governedGraphPrepareInvokeButton').textContent === 'Prepare invocation'); }})()", _visibleGovernedInvocationTimeout);
        }
        catch (TimeoutException exception)
        {
            using var diagnosticTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string status;
            string runProjection;
            try
            {
                status = await browser.EvaluateStringAsync("document.getElementById('governedGraphInvocationStatus').textContent", diagnosticTimeout.Token);
                runProjection = await browser.EvaluateStringAsync("JSON.stringify([...document.querySelectorAll('#runList .run-id')].map((element) => element.textContent ?? ''))", diagnosticTimeout.Token);
            }
            catch (OperationCanceledException) when (diagnosticTimeout.IsCancellationRequested)
            {
                status = "<diagnostic read timed out>";
                runProjection = "<diagnostic read timed out>";
            }

            throw new TimeoutException($"The visible governed invocation did not reach a conclusive bounded outcome. Status: {status}. Run projection: {runProjection}", exception);
        }

        var invocationStatus = await browser.EvaluateStringAsync("document.getElementById('governedGraphInvocationStatus').textContent");
        var visibleRunIds = JsonSerializer.Deserialize<string[]>(await browser.EvaluateStringAsync("JSON.stringify([...document.querySelectorAll('#runList .run-id')].map((element) => element.textContent ?? ''))")) ?? [];
        var selectedRunIds = JsonSerializer.Deserialize<string[]>(await browser.EvaluateStringAsync("JSON.stringify([...document.querySelectorAll('#runList .run-item.selected .run-id')].map((element) => element.textContent ?? ''))")) ?? [];
        var runId = GovernedVisibleRunReadiness.RequireNewSelectedRunId(invocationStatus, baselineRunIds, visibleRunIds, selectedRunIds);
        using var exactRunReadTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var exactRun = JsonDocument.Parse(await ReadRunFromBrowserAsync(browser, runId, exactRunReadTimeout.Token));
        Assert.Equal(expectedOperationId, exactRun.RootElement.GetProperty("admissionOperationId").GetString());
        Assert.Equal(expectedGraphId, exactRun.RootElement.GetProperty("loopId").GetString());
        var binding = exactRun.RootElement.GetProperty("frontier").GetProperty("binding");
        Assert.Equal(expectedGraphId, binding.GetProperty("graphId").GetString());
        Assert.Equal(expectedRevisionId, binding.GetProperty("revisionId").GetString());
        return runId;
    }

    private static Task<string> ReadRunFromBrowserAsync(HeadlessBrowserSession browser, string runId, CancellationToken cancellationToken = default)
    {
        var runUrl = JsonSerializer.Serialize($"/api/loop-runs/{runId}");
        return browser.EvaluateStringAsync($"(async () => {{ const response = await fetch({runUrl}); if (!response.ok) throw new Error(`run read ${{response.status}}`); return JSON.stringify(await response.json()); }})()", cancellationToken);
    }

    private static async Task<string> WaitForTerminalRunFromBrowserAsync(HeadlessBrowserSession browser, string runId)
    {
        using var timeout = new CancellationTokenSource(_visibleGovernedInvocationTimeout);
        string? latestSerialized = null;
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                latestSerialized = await ReadRunFromBrowserAsync(browser, runId, timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            using var run = JsonDocument.Parse(latestSerialized);
            var status = run.RootElement.GetProperty("status").GetString();
            if (status is nameof(CustomLoopRunStatus.Completed)
                or nameof(CustomLoopRunStatus.Failed)
                or nameof(CustomLoopRunStatus.Cancelled)
                or nameof(CustomLoopRunStatus.NeedsReview))
            {
                return latestSerialized;
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

        throw new TimeoutException($"Run `{runId}` did not reach a terminal status through the visible Runs inspection surface. Last run: {latestSerialized}");
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

    private static async Task<string> WaitForFakeCodexEventAsync(
        TestWorkspace workspace,
        string prompt,
        string stage,
        IReadOnlySet<string> priorProviderInstances,
        string? processInstanceId = null,
        bool? approved = null)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        ArgumentNullException.ThrowIfNull(priorProviderInstances);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var events = await ReadFakeCodexTraceAsync(workspace);
            var candidate = processInstanceId ?? SelectFreshFakeCodexAttemptInstance(events, prompt, priorProviderInstances);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var candidateEvents = events.Where(item => string.Equals(TraceString(item, "instanceId"), candidate, StringComparison.Ordinal)).ToArray();
                var requiredStages = RequiredFakeCodexStages(stage);
                var matchedEvents = new List<JsonElement>(requiredStages.Length);
                var nextEventIndex = 0;
                string? missingStage = null;
                foreach (var requiredStage in requiredStages)
                {
                    var eventIndex = FindFakeCodexStageIndex(candidateEvents, requiredStage, prompt, requiredStage == stage ? approved : null, nextEventIndex);
                    if (eventIndex < 0)
                    {
                        missingStage = requiredStage;
                        break;
                    }

                    matchedEvents.Add(candidateEvents[eventIndex]);
                    nextEventIndex = eventIndex + 1;
                }

                if (missingStage is null)
                {
                    ValidateFakeCodexAttemptTrace(matchedEvents, prompt, approved);
                    return candidate;
                }

                if (candidateEvents.Any(item => string.Equals(TraceString(item, "stage"), "process-error", StringComparison.Ordinal)
                    || string.Equals(TraceString(item, "stage"), "process-exit", StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException($"Fake Codex provider fixture terminated before stage {missingStage} for prompt {prompt}.{Environment.NewLine}{await ReadFakeCodexTraceTextAsync(workspace)}");
                }
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

        throw new TimeoutException($"Fake Codex provider fixture did not emit stage {stage} for prompt {prompt}.{Environment.NewLine}{await ReadFakeCodexTraceTextAsync(workspace)}");
    }

    private static string[] RequiredFakeCodexStages(string stage)
    {
        var requiredStages = new List<string> { "process-start", "initialize", "thread-start", "turn-start" };
        if (string.Equals(stage, "tool-call", StringComparison.Ordinal)
            || string.Equals(stage, "tool-response", StringComparison.Ordinal)
            || string.Equals(stage, "turn-completed", StringComparison.Ordinal))
        {
            requiredStages.Add("tool-call");
        }

        if (string.Equals(stage, "tool-response", StringComparison.Ordinal)
            || string.Equals(stage, "turn-completed", StringComparison.Ordinal))
        {
            requiredStages.Add("tool-response");
        }

        requiredStages.Add(stage);
        return requiredStages.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string? SelectFreshFakeCodexAttemptInstance(JsonElement[] events, string prompt, IReadOnlySet<string> priorProviderInstances)
    {
        var candidates = events
            .Where(item => string.Equals(TraceString(item, "stage"), "process-start", StringComparison.Ordinal))
            .Select(item => TraceString(item, "instanceId"))
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId) && !priorProviderInstances.Contains(instanceId!))
            .Select(instanceId => instanceId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var promptCandidates = candidates.Where(instanceId => HasFakeCodexPromptStage(events, instanceId, "turn-start", prompt)).ToArray();
        if (promptCandidates.Length > 1)
        {
            throw new InvalidOperationException($"Multiple fresh Fake Codex provider instances handled the exact prompt `{prompt}`: {string.Join(", ", promptCandidates)}.");
        }

        if (promptCandidates.Length == 1)
        {
            return promptCandidates[0];
        }

        var liveAttemptCandidates = candidates
            .Where(instanceId => !HasFakeCodexTerminalStage(events, instanceId)
                && (HasFakeCodexStage(events, instanceId, "thread-start")
                    || (HasFakeCodexStage(events, instanceId, "initialize")
                        && !HasFakeCodexStage(events, instanceId, "model-list"))))
            .ToArray();
        return liveAttemptCandidates.Length == 1 ? liveAttemptCandidates[0] : null;
    }

    private static bool HasFakeCodexPromptStage(JsonElement[] events, string instanceId, string stage, string prompt)
        => events.Any(item => string.Equals(TraceString(item, "instanceId"), instanceId, StringComparison.Ordinal)
            && string.Equals(TraceString(item, "stage"), stage, StringComparison.Ordinal)
            && string.Equals(TraceString(item, "prompt"), prompt, StringComparison.Ordinal));

    private static bool HasFakeCodexStage(JsonElement[] events, string instanceId, string stage)
        => events.Any(item => string.Equals(TraceString(item, "instanceId"), instanceId, StringComparison.Ordinal)
            && string.Equals(TraceString(item, "stage"), stage, StringComparison.Ordinal));

    private static bool HasFakeCodexTerminalStage(JsonElement[] events, string instanceId)
        => events.Any(item => string.Equals(TraceString(item, "instanceId"), instanceId, StringComparison.Ordinal)
            && (string.Equals(TraceString(item, "stage"), "process-error", StringComparison.Ordinal)
                || string.Equals(TraceString(item, "stage"), "process-exit", StringComparison.Ordinal)));

    private static void ValidateFakeCodexAttemptTrace(IReadOnlyList<JsonElement> events, string prompt, bool? approved)
    {
        var turnStart = events.Single(item => string.Equals(TraceString(item, "stage"), "turn-start", StringComparison.Ordinal));
        var threadId = TraceString(turnStart, "threadId");
        var turnId = TraceString(turnStart, "turnId");
        Assert.False(string.IsNullOrWhiteSpace(threadId));
        Assert.False(string.IsNullOrWhiteSpace(turnId));
        Assert.Equal(prompt, TraceString(turnStart, "prompt"));

        var toolCall = events.SingleOrDefault(item => string.Equals(TraceString(item, "stage"), "tool-call", StringComparison.Ordinal));
        if (toolCall.ValueKind != JsonValueKind.Undefined)
        {
            Assert.Equal(threadId, TraceString(toolCall, "threadId"));
            Assert.Equal(turnId, TraceString(toolCall, "turnId"));
            Assert.Equal(prompt, TraceString(toolCall, "prompt"));
            Assert.Equal("embodysense", TraceString(toolCall, "namespace"));
            Assert.Equal("command", TraceString(toolCall, "tool"));
            Assert.Equal("approval-note.txt", TraceString(toolCall, "path"));
            Assert.False(string.IsNullOrWhiteSpace(TraceString(toolCall, "callId")));
        }

        var toolResponse = events.SingleOrDefault(item => string.Equals(TraceString(item, "stage"), "tool-response", StringComparison.Ordinal));
        if (toolResponse.ValueKind != JsonValueKind.Undefined)
        {
            Assert.NotEqual(JsonValueKind.Undefined, toolCall.ValueKind);
            Assert.Equal(threadId, TraceString(toolResponse, "threadId"));
            Assert.Equal(turnId, TraceString(toolResponse, "turnId"));
            Assert.Equal(TraceString(toolCall, "callId"), TraceString(toolResponse, "callId"));
            Assert.Equal(prompt, TraceString(toolResponse, "prompt"));
            Assert.Equal(approved, TraceBoolean(toolResponse, "approved"));
            Assert.Equal(approved, TraceBoolean(toolResponse, "success"));
            Assert.Equal(approved == true ? "succeeded" : "approvalrejected", TraceString(toolResponse, "brokerOutcome"));
        }

        var turnCompleted = events.SingleOrDefault(item => string.Equals(TraceString(item, "stage"), "turn-completed", StringComparison.Ordinal));
        if (turnCompleted.ValueKind != JsonValueKind.Undefined)
        {
            Assert.Equal(threadId, TraceString(turnCompleted, "threadId"));
            Assert.Equal(turnId, TraceString(turnCompleted, "turnId"));
            Assert.Equal(prompt, TraceString(turnCompleted, "prompt"));
            Assert.Equal(approved == true ? "approved" : "rejected", TraceString(turnCompleted, "outcome"));
        }

        var turnFailed = events.SingleOrDefault(item => string.Equals(TraceString(item, "stage"), "turn-failed", StringComparison.Ordinal));
        if (turnFailed.ValueKind != JsonValueKind.Undefined)
        {
            Assert.Equal(threadId, TraceString(turnFailed, "threadId"));
            Assert.Equal(turnId, TraceString(turnFailed, "turnId"));
            Assert.Equal(prompt, TraceString(turnFailed, "prompt"));
            Assert.Equal("controlled browser provider failure", TraceString(turnFailed, "detail"));
        }
    }

    private static int FindFakeCodexStageIndex(JsonElement[] events, string stage, string prompt, bool? approved, int startIndex)
    {
        for (var index = startIndex; index < events.Length; index++)
        {
            var item = events[index];
            if (!string.Equals(TraceString(item, "stage"), stage, StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(stage, "process-start", StringComparison.Ordinal)
                && !string.Equals(stage, "initialize", StringComparison.Ordinal)
                && !string.Equals(stage, "thread-start", StringComparison.Ordinal)
                && !string.Equals(TraceString(item, "prompt"), prompt, StringComparison.Ordinal))
            {
                continue;
            }

            if (approved.HasValue && TraceBoolean(item, "approved") != approved.Value)
            {
                continue;
            }

            return index;
        }

        return -1;
    }

    private static async Task<HashSet<string>> ReadFakeCodexProcessInstancesAsync(TestWorkspace workspace)
    {
        var events = await ReadFakeCodexTraceAsync(workspace);
        return events
            .Select(item => TraceString(item, "instanceId"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task AssertOnlyExpectedFreshFakeCodexAttemptAsync(TestWorkspace workspace, IReadOnlySet<string> priorProviderInstances, string expectedProviderInstance)
    {
        var events = await ReadFakeCodexTraceAsync(workspace);
        var freshAttemptInstances = events
            .Where(item => string.Equals(TraceString(item, "stage"), "thread-start", StringComparison.Ordinal))
            .Select(item => TraceString(item, "instanceId"))
            .Where(instanceId => !string.IsNullOrWhiteSpace(instanceId) && !priorProviderInstances.Contains(instanceId!))
            .Select(instanceId => instanceId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal([expectedProviderInstance], freshAttemptInstances);
    }

    private static async Task<JsonElement[]> ReadFakeCodexTraceAsync(TestWorkspace workspace)
    {
        var path = FakeCodexExecutable.ProtocolTracePath(workspace);
        if (!File.Exists(path))
        {
            return [];
        }

        var events = new List<JsonElement>();
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    events.Add(document.RootElement.Clone());
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (IOException)
        {
        }

        return events.ToArray();
    }

    private static async Task<string> ReadFakeCodexTraceTextAsync(TestWorkspace workspace)
    {
        var path = FakeCodexExecutable.ProtocolTracePath(workspace);
        if (!File.Exists(path))
        {
            return $"Trace file was not created: {path}";
        }

        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (IOException exception)
        {
            return $"Trace file could not be read: {path}{Environment.NewLine}{exception}";
        }
    }

    private static string? TraceString(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? TraceBoolean(JsonElement value, string propertyName)
    {
        return value.TryGetProperty(propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
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

    private static async Task SetGovernedGraphInspectorParameterAsync(HeadlessBrowserSession browser, string parameterId, string value)
    {
        var jsonParameterId = JsonSerializer.Serialize(parameterId);
        var jsonValue = JsonSerializer.Serialize(value);
        await browser.EvaluateAsync("(() => { const label = [...document.querySelectorAll('#governedGraphInspector label')].find((item) => item.querySelector('span')?.textContent?.startsWith(" + jsonParameterId + ")); const input = label?.querySelector('input, select'); if (!input) throw new Error(`Governed graph parameter ${" + jsonParameterId + "} was not rendered.`); input.value = " + jsonValue + "; input.dispatchEvent(new Event('input', { bubbles: true })); })()");
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

    private sealed partial class HeadlessBrowserSession : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ClientWebSocket _socket;
        private readonly int _debugPort;
        private readonly string _userDataDirectory;
        private readonly BoundedProcessOutput _output;
        private readonly BoundedProcessOutput _error;
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingCommands = new();
        private readonly PendingBrowserCommandResponses _pendingResponseHandlers = new();
        private readonly ConcurrentDictionary<int, Task> _pendingSends = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _diagnosticsGate = new();
        private readonly List<string> _diagnostics = [];
        private readonly byte[] _buffer = new byte[65536];
        private readonly Task _readerTask;
        private readonly ExpectedServerRestartRequestTracker _requestTracker;
        private Exception? _readerFailure;
        private int _acceptNextJavaScriptDialog;
        private int _nextCommandId;
        private int _disposed;

        private HeadlessBrowserSession(Process process, ClientWebSocket socket, string userDataDirectory, BoundedProcessOutput output, BoundedProcessOutput error, string targetUrl, int debugPort)
        {
            _process = process;
            _socket = socket;
            _debugPort = debugPort;
            _userDataDirectory = userDataDirectory;
            _output = output;
            _error = error;
            _requestTracker = new ExpectedServerRestartRequestTracker(new Uri(targetUrl).Authority);
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
                session = new HeadlessBrowserSession(process, socket, userDataDirectory, output, error, targetUrl, debugPort);
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

        public Task WaitForExpressionAsync(string expression)
        {
            return WaitForExpressionAsync(expression, TimeSpan.FromSeconds(30));
        }

        public async Task WaitForExpressionAsync(string expression, TimeSpan timeoutValue)
        {
            Exception? lastException = null;
            using var timeout = new CancellationTokenSource(timeoutValue);
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

        public async Task<string> EvaluateStringAsync(string expression, CancellationToken cancellationToken = default)
        {
            var value = await EvaluateAsync(expression, cancellationToken);
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

        public async Task BeginExpectedServerRestartAsync(CancellationToken cancellationToken = default)
        {
            _requestTracker.PrepareExpectedServerRestart();
            using var barrierTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            barrierTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                _ = await EvaluateAsync("true", barrierTimeout.Token, responseHandler: FreezeExpectedServerRestartAtBarrierResponse);
            }
            catch
            {
                _requestTracker.AbortExpectedServerRestart();
                throw;
            }
        }

        public void MarkExpectedReplacementServerStarting()
        {
            _requestTracker.MarkExpectedReplacementServerStarting();
        }

        public async Task EndExpectedServerRestartAsync(CancellationToken cancellationToken = default)
        {
            using var barrierTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            barrierTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                _ = await EvaluateAsync("true", barrierTimeout.Token, responseHandler: EndExpectedServerRestartAtBarrierResponse);
            }
            catch
            {
                _requestTracker.AbortExpectedServerRestart();
                throw;
            }
        }

        public async Task AssertHealthyAsync(params (string UrlFragment, int StatusCode)[] expectedHttpFailures)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _ = await EvaluateAsync("true", timeout.Token);
            Assert.False(_process.HasExited, $"Browser process exited unexpectedly.{Environment.NewLine}{FormatOutput()}");
            Assert.Null(_readerFailure);
            var diagnostics = GetDiagnosticsSnapshot().ToList();
            foreach (var expected in expectedHttpFailures)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(expected.UrlFragment);
                Assert.InRange(expected.StatusCode, 400, 599);
                var removed = diagnostics.RemoveAll(item => item.Contains(expected.UrlFragment, StringComparison.Ordinal)
                    && (item.Contains($"\"status\":{expected.StatusCode}", StringComparison.Ordinal)
                        || item.Contains($"status of {expected.StatusCode} (", StringComparison.Ordinal)));
                Assert.True(removed > 0, $"The expected browser HTTP {expected.StatusCode} failure for `{expected.UrlFragment}` was not observed.");
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

            await DisposeChildTabsAsync();

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

        private async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken, bool userGesture = false, Action<JsonElement>? responseHandler = null)
        {
            var response = await SendCommandAsync("Runtime.evaluate", new
            {
                expression,
                awaitPromise = true,
                returnByValue = true,
                userGesture
            }, cancellationToken, responseHandler);
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

        private void FreezeExpectedServerRestartAtBarrierResponse(JsonElement response)
        {
            if (response.TryGetProperty("exceptionDetails", out _)
                || !response.TryGetProperty("result", out var commandResult)
                || !commandResult.TryGetProperty("result", out _))
            {
                throw new InvalidOperationException("Browser restart receive-loop barrier command failed: " + response.GetRawText());
            }

            _requestTracker.FreezeExpectedServerRestart();
        }

        private void EndExpectedServerRestartAtBarrierResponse(JsonElement response)
        {
            if (response.TryGetProperty("exceptionDetails", out _)
                || !response.TryGetProperty("result", out var commandResult)
                || !commandResult.TryGetProperty("result", out _))
            {
                throw new InvalidOperationException("Browser restart completion barrier command failed: " + response.GetRawText());
            }

            _requestTracker.EndExpectedServerRestart();
        }

        private async Task<JsonElement> SendCommandAsync(string method, object? parameters = null, CancellationToken cancellationToken = default, Action<JsonElement>? responseHandler = null)
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
                if (responseHandler is not null)
                {
                    _pendingResponseHandlers.Add(commandId, responseHandler);
                }

                var sendTask = SendPayloadAsync(bytes);
                _pendingSends[commandId] = sendTask;
                _ = ObserveSendCompletionAsync(commandId, sendTask);
                await sendTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _pendingCommands.TryRemove(commandId, out _);
                _pendingResponseHandlers.Remove(commandId);
                throw;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or InvalidOperationException or ObjectDisposedException)
            {
                _pendingCommands.TryRemove(commandId, out _);
                _pendingResponseHandlers.Remove(commandId);
                throw new InvalidOperationException("Browser DevTools command send failed." + Environment.NewLine + FormatOutput(), exception);
            }

            try
            {
                return await completion.Task.WaitAsync(cancellationToken);
            }
            catch
            {
                _pendingCommands.TryRemove(commandId, out _);
                _pendingResponseHandlers.Remove(commandId);
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
                            try
                            {
                                _pendingResponseHandlers.Handle(commandId, root);
                                completion.TrySetResult(root.Clone());
                            }
                            catch (Exception exception)
                            {
                                completion.TrySetException(exception);
                            }
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
                        _pendingResponseHandlers.Remove(pending.Key);
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

            if (method == "Network.loadingFailed")
            {
                var requestId = parameters.TryGetProperty("requestId", out var requestIdValue) && requestIdValue.ValueKind == JsonValueKind.String
                    ? requestIdValue.GetString()
                    : null;
                var canceled = parameters.TryGetProperty("canceled", out var canceledValue) && canceledValue.ValueKind == JsonValueKind.True;
                var errorText = parameters.TryGetProperty("errorText", out var errorTextValue) ? errorTextValue.GetString() : null;
                if (!_requestTracker.ProcessLoadingFailed(requestId, canceled, errorText))
                {
                    AddDiagnostic("network load failed: " + parameters.GetRawText());
                }

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
                if (!parameters.TryGetProperty("exceptionDetails", out var exceptionDetails)
                    || exceptionDetails.ValueKind != JsonValueKind.Object)
                {
                    AddDiagnostic("page exception: " + parameters.GetRawText());
                    return;
                }

                var text = exceptionDetails.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String ? textValue.GetString() : null;
                var url = exceptionDetails.TryGetProperty("url", out var urlValue) && urlValue.ValueKind == JsonValueKind.String ? urlValue.GetString() : null;
                var description = exceptionDetails.TryGetProperty("exception", out var exceptionValue)
                    && exceptionValue.ValueKind == JsonValueKind.Object
                    && exceptionValue.TryGetProperty("description", out var descriptionValue)
                    && descriptionValue.ValueKind == JsonValueKind.String
                        ? descriptionValue.GetString()
                        : null;
                var className = exceptionValue.ValueKind == JsonValueKind.Object
                    && exceptionValue.TryGetProperty("className", out var classNameValue)
                    && classNameValue.ValueKind == JsonValueKind.String
                        ? classNameValue.GetString()
                        : null;
                var stackFrame = exceptionDetails.TryGetProperty("stackTrace", out var stackTrace)
                    && stackTrace.ValueKind == JsonValueKind.Object
                    && stackTrace.TryGetProperty("callFrames", out var callFrames)
                    && callFrames.ValueKind == JsonValueKind.Array
                    && callFrames.GetArrayLength() > 0
                        ? callFrames[0]
                        : default;
                var functionName = stackFrame.ValueKind == JsonValueKind.Object
                    && stackFrame.TryGetProperty("functionName", out var functionNameValue)
                    && functionNameValue.ValueKind == JsonValueKind.String
                        ? functionNameValue.GetString()
                        : null;
                if (ExpectedServerRestartDiagnosticClassifier.IsExpectedServerRestartPageException(
                    _requestTracker.IsExpectedServerRestart(),
                    text,
                    className,
                    description,
                    functionName,
                    url,
                    _requestTracker.TargetAuthority))
                {
                    return;
                }

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
                _requestTracker.Track(requestId, requestUrl.GetString()!);
                return;
            }

            if (method == "Network.webSocketCreated"
                && parameters.TryGetProperty("url", out var websocketUrl)
                && websocketUrl.ValueKind == JsonValueKind.String)
            {
                _requestTracker.Track(requestId, websocketUrl.GetString()!);
                return;
            }

            if (method is "Network.loadingFinished" or "Network.webSocketClosed")
            {
                _requestTracker.Complete(requestId);
            }
        }

        private bool IsExpectedServerRestartLogEntry(JsonElement entry)
        {
            var requestId = entry.TryGetProperty("networkRequestId", out var requestIdValue) && requestIdValue.ValueKind == JsonValueKind.String
                ? requestIdValue.GetString()
                : null;
            var source = entry.TryGetProperty("source", out var sourceValue) ? sourceValue.GetString() : null;
            var text = entry.TryGetProperty("text", out var textValue) ? textValue.GetString() : null;
            var url = entry.TryGetProperty("url", out var urlValue) ? urlValue.GetString() : null;
            return _requestTracker.IsExpectedServerRestartLogEntry(requestId, source, text, url);
        }

        private bool IsExpectedServerRestartHttpResponse(JsonElement response, double statusCode)
        {
            return statusCode == 401
                && _requestTracker.IsExpectedServerRestart()
                && response.TryGetProperty("url", out var url)
                && url.ValueKind == JsonValueKind.String
                && ContainsTargetAuthority(url.GetString());
        }

        private bool ContainsTargetAuthority(string? value)
        {
            return value?.Contains(_requestTracker.TargetAuthority, StringComparison.OrdinalIgnoreCase) == true;
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
