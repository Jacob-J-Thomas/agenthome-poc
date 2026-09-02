using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    private const string HumanReviewResponseLossBrowserProfileId = "org.example/model-profile/human-review-response-loss";

    [InstalledBrowserFact]
    public async Task Human_review_browser_retries_a_committed_response_loss_with_the_original_operation_and_lifecycle()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = new BrowserModelProfileSpec(HumanReviewResponseLossBrowserProfileId, "human-review-response-loss", "Test-only response-loss Human Review model profile.", "gpt-test", true);
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        var runId = "browser-human-review-response-loss";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "committed response loss", includePreDispatchEffect: true, capabilityTrustRoot: capabilityTrustRoot);

        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile], suppressGovernedBackgroundHost: true);
        HeadlessBrowserSession? browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        string? retiredServerOutput = null;
        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewResponseLossAsync(browser);
            await SelectHumanReviewResponseLossAsync(browser, runId);
            using var initialDocument = JsonDocument.Parse(await ReadHumanReviewResponseLossAsync(browser, runId));
            var initialSummary = initialDocument.RootElement.GetProperty("detail").GetProperty("summary");
            var initialRequestId = initialSummary.GetProperty("requestId").GetString();
            var initialRequestHash = initialSummary.GetProperty("requestHash").GetString();
            var initialLifecycleVersion = initialSummary.GetProperty("lifecycleVersion").GetInt64();
            Assert.False(string.IsNullOrWhiteSpace(initialRequestId));
            Assert.Matches("^[0-9a-f]{64}$", initialRequestHash ?? string.Empty);
            Assert.True(initialLifecycleVersion > 0);
            var actionPath = $"/api/human-reviews/{Uri.EscapeDataString(runId)}/approve";
            await browser.EvaluateWithUserGestureAsync(HumanReviewBrowserResponseLossScripts.InstallPostCommitResponseLoss(actionPath));
            await ClickAsync(browser, "[data-testid=\"human-review-approve\"]");
            await browser.WaitForExpressionAsync("window.__humanReviewResponseLoss?.networkPosts === 1");
            var responseLossResult = await browser.EvaluateStringAsync("JSON.stringify({ status: window.__humanReviewResponseLoss.realCommitStatus, mode: window.__humanReviewResponseLoss.mode })");
            Assert.True(await browser.EvaluateBooleanAsync("window.__humanReviewResponseLoss.realCommitStatus === 200 && window.__humanReviewResponseLoss.mode === 'response-lost'"), responseLossResult);
            var approvedLifecycleVersion = await WaitForCanonicalHumanReviewResponseLossAsync(browser, runId, initialRequestId!, initialRequestHash!, "approved", 1, initialLifecycleVersion + 1);
            using var afterLossDocument = JsonDocument.Parse(await ReadHumanReviewResponseLossAsync(browser, runId));
            AssertHumanReviewIdentity(afterLossDocument, runId, initialRequestId!, initialRequestHash!, approvedLifecycleVersion);
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewActionStatus').textContent.includes('recorded decision response remains unresolved')");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-approve\"]')?.textContent.toLowerCase().includes('retry recorded approve') && document.querySelector('[data-testid=\"human-review-approve\"]')?.disabled === false"));
            Assert.True(await browser.EvaluateBooleanAsync("window.__humanReviewResponseLoss.canonicalGets >= 3"));

            using var firstPayloadDocument = JsonDocument.Parse(await browser.EvaluateStringAsync("window.__humanReviewResponseLoss.payloads[0]"));
            var firstOperationId = firstPayloadDocument.RootElement.GetProperty("operationId").GetString();
            var firstLifecycleVersion = firstPayloadDocument.RootElement.GetProperty("expectedLifecycleVersion").GetInt64();
            Assert.False(string.IsNullOrWhiteSpace(firstOperationId));
            Assert.Equal(initialLifecycleVersion, firstLifecycleVersion);
            await AssertValueFreeHumanReviewOperationStorageAsync(browser, firstOperationId!, firstLifecycleVersion, initialRequestId!, initialRequestHash!, runId);

            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewResponseLossAsync(browser);
            await SelectHumanReviewResponseLossAsync(browser, runId);
            var reloadedLifecycleVersion = await WaitForCanonicalHumanReviewResponseLossAsync(browser, runId, initialRequestId!, initialRequestHash!, "approved", 1, approvedLifecycleVersion);
            Assert.Equal(approvedLifecycleVersion, reloadedLifecycleVersion);
            using var afterReloadDocument = JsonDocument.Parse(await ReadHumanReviewResponseLossAsync(browser, runId));
            AssertHumanReviewIdentity(afterReloadDocument, runId, initialRequestId!, initialRequestHash!, reloadedLifecycleVersion);
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewActionStatus').textContent.includes('recorded decision response remains unresolved')");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-approve\"]')?.textContent.toLowerCase().includes('retry recorded approve') && document.querySelector('[data-testid=\"human-review-approve\"]')?.disabled === false"));
            await browser.EvaluateWithUserGestureAsync(HumanReviewBrowserResponseLossScripts.InstallRetryCapture(actionPath));
            await ClickAsync(browser, "[data-testid=\"human-review-approve\"]");
            await browser.WaitForExpressionAsync("window.__humanReviewRetryCapture?.statuses.length === 1 && window.__humanReviewRetryCapture.statuses[0] === 200");
            var replayedLifecycleVersion = await WaitForCanonicalHumanReviewResponseLossAsync(browser, runId, initialRequestId!, initialRequestHash!, "approved", 1, reloadedLifecycleVersion);
            Assert.Equal(reloadedLifecycleVersion, replayedLifecycleVersion);

            using var secondPayloadDocument = JsonDocument.Parse(await browser.EvaluateStringAsync("window.__humanReviewRetryCapture.payloads[0]"));
            Assert.Equal(firstOperationId, secondPayloadDocument.RootElement.GetProperty("operationId").GetString());
            Assert.Equal(firstLifecycleVersion, secondPayloadDocument.RootElement.GetProperty("expectedLifecycleVersion").GetInt64());
            var retryStatus = await browser.EvaluateStringAsync("document.getElementById('humanReviewActionStatus').textContent");
            Assert.Contains("already recorded", retryStatus, StringComparison.OrdinalIgnoreCase);
            Assert.True(await browser.EvaluateBooleanAsync("!Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index)).some(key => key?.startsWith('embodysense.human-review.operations.v1.'))"));

            using var reviewDocument = JsonDocument.Parse(await ReadHumanReviewResponseLossAsync(browser, runId));
            Assert.Equal(1, reviewDocument.RootElement.GetProperty("detail").GetProperty("decisions").GetArrayLength());
            Assert.Equal(firstOperationId, reviewDocument.RootElement.GetProperty("detail").GetProperty("decisions")[0].GetProperty("operationId").GetString());
            await AssertHumanReviewStillBlockedBeforeRecoveryAsync(paths, runId);
            await browser.BeginExpectedServerRestartAsync();
            retiredServerOutput = app.FormatOutput();
            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent === 'Web primary'");
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await browser.EndExpectedServerRestartAsync();
            var durable = await WaitForCompletedHumanReviewResponseLossAsync(paths, runId);
            await AssertSingleApprovedPreDispatchEffectAsync(paths, durable);
            app.AssertHealthy();
            await AssertHumanReviewResponseLossBrowserHealthyAsync(browser, runId);
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_retries_a_committed_response_loss_with_the_original_operation_and_lifecycle), browser, app, retiredServerOutput);
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

    private static async Task OpenHumanReviewResponseLossAsync(HeadlessBrowserSession browser)
    {
        await ClickAsync(browser, "[data-testid=\"human-review-nav\"]");
        await browser.WaitForExpressionAsync("!document.getElementById('humanReviewView').hidden && document.getElementById('humanReviewListStatus').textContent.length > 0");
    }

    private static async Task SelectHumanReviewResponseLossAsync(HeadlessBrowserSession browser, string runId)
    {
        var selector = JsonSerializer.Serialize($"[data-testid=\"human-review-item\"][data-run-id=\"{runId}\"]");
        await browser.EvaluateWithUserGestureAsync($"(() => {{ const item = document.querySelector({selector}); if (!item) throw new Error('Response-loss Human Review item was not rendered.'); item.click(); }})()");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await browser.EvaluateBooleanAsync("document.getElementById('humanReviewDetailPanel').hidden === false && document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread') && document.querySelector('[data-testid=\"human-review-approve\"]')?.disabled === false").ConfigureAwait(false))
            {
                return;
            }

            if (await browser.EvaluateBooleanAsync("document.getElementById('humanReviewDetailStatus').textContent.toLowerCase().includes('temporarily unavailable')").ConfigureAwait(false))
            {
                await ClickAsync(browser, "[data-testid=\"human-review-detail-refresh\"]").ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException("The response-loss Human Review detail did not reach a canonical ready state.");
    }

    private static async Task<long> WaitForCanonicalHumanReviewResponseLossAsync(HeadlessBrowserSession browser, string runId, string expectedRequestId, string expectedRequestHash, string lifecycle, int decisionCount, long minimumLifecycleVersion)
    {
        var lifecycleToken = JsonSerializer.Serialize(lifecycle.Replace('-', ' '));
        var runIdToken = JsonSerializer.Serialize(runId);
        var requestIdToken = JsonSerializer.Serialize(expectedRequestId);
        var requestHashPrefixToken = JsonSerializer.Serialize(expectedRequestHash[..12]);
        var lastObservation = "none";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var response = await ReadHumanReviewResponseLossHttpAsync(browser, runId, timeout.Token).ConfigureAwait(false);
                lastObservation = DescribeHumanReviewResponseLoss(response.Status, response.Body);
                if (response.Status != 200)
                {
                    await Task.Delay(100, timeout.Token).ConfigureAwait(false);
                    continue;
                }

                using var document = JsonDocument.Parse(response.Body);
                var root = document.RootElement;
                var detail = root.GetProperty("detail");
                var summary = detail.GetProperty("summary");
                var lifecycleVersion = summary.GetProperty("lifecycleVersion").GetInt64();
                var continuationStatus = detail.GetProperty("runtime").GetProperty("continuationStatus").GetString();
                var canonical = string.Equals(root.GetProperty("status").GetString(), "ready", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(summary.GetProperty("runId").GetString(), runId, StringComparison.Ordinal)
                    && string.Equals(summary.GetProperty("requestId").GetString(), expectedRequestId, StringComparison.Ordinal)
                    && string.Equals(summary.GetProperty("requestHash").GetString(), expectedRequestHash, StringComparison.Ordinal)
                    && string.Equals(summary.GetProperty("lifecycleStatus").GetString(), lifecycle, StringComparison.OrdinalIgnoreCase)
                    && lifecycleVersion >= minimumLifecycleVersion
                    && detail.GetProperty("decisions").GetArrayLength() == decisionCount
                    && continuationStatus == "reserved";
                if (canonical)
                {
                    var rendered = await browser.EvaluateBooleanAsync($"document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread') && document.getElementById('humanReviewIdentity').textContent.includes({requestIdToken}) && document.getElementById('humanReviewIdentity').textContent.includes({requestHashPrefixToken}) && document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes({lifecycleToken}) && Array.from(document.querySelectorAll('#humanReviewSummary div')).some(item => item.querySelector('dt')?.textContent === 'Lifecycle version' && item.querySelector('dd')?.textContent === '{lifecycleVersion}') && document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length === {decisionCount}", timeout.Token).ConfigureAwait(false);
                    if (rendered)
                    {
                        return lifecycleVersion;
                    }

                    var listMatchesCanonical = await browser.EvaluateBooleanAsync($"Array.from(document.querySelectorAll('[data-testid=\"human-review-item\"]')).some(item => item.dataset.runId === {runIdToken} && item.textContent.includes('version {lifecycleVersion}'))", timeout.Token).ConfigureAwait(false);
                    if (!listMatchesCanonical)
                    {
                        // The detail endpoint may advance before the catalog response. Refresh the visible
                        // catalog first so the selected summary and detail identity advance together.
                        await ClickHumanReviewResponseLossAsync(browser, "[data-testid=\"human-review-refresh\"]", timeout.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await ClickHumanReviewResponseLossAsync(browser, "[data-testid=\"human-review-detail-refresh\"]", timeout.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (JsonException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
            try
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"The response-loss Human Review reread did not converge on the expected canonical lifecycle ({lastObservation}).");
    }

    private static Task<string> ReadHumanReviewResponseLossAsync(HeadlessBrowserSession browser, string runId)
    {
        var route = JsonSerializer.Serialize($"/api/human-reviews/{Uri.EscapeDataString(runId)}");
        return browser.EvaluateStringAsync($"(async () => {{ const response = await fetch({route}, {{ cache: 'no-store' }}); if (!response.ok) throw new Error('Human Review read failed: ' + response.status); return JSON.stringify(await response.json()); }})()");
    }

    private static async Task<(int Status, string Body)> ReadHumanReviewResponseLossHttpAsync(HeadlessBrowserSession browser, string runId, CancellationToken cancellationToken)
    {
        var route = JsonSerializer.Serialize($"/api/human-reviews/{Uri.EscapeDataString(runId)}");
        var result = await browser.EvaluateStringAsync($"(async () => {{ const controller = new AbortController(); const timeout = setTimeout(() => controller.abort(), 2000); try {{ const response = await fetch({route}, {{ cache: 'no-store', signal: controller.signal }}); return JSON.stringify({{ status: response.status, body: await response.text() }}); }} catch (error) {{ return JSON.stringify({{ status: 0, body: 'fetch-error:' + (error?.name ?? 'unknown') }}); }} finally {{ clearTimeout(timeout); }} }})()", cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(result);
        var root = document.RootElement;
        return (root.GetProperty("status").GetInt32(), root.GetProperty("body").GetString() ?? string.Empty);
    }

    private static Task ClickHumanReviewResponseLossAsync(HeadlessBrowserSession browser, string selector, CancellationToken cancellationToken)
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        return browser.EvaluateWithUserGestureAsync("(() => { const element = document.querySelector(" + jsonSelector + "); if (!element) throw new Error('Element was not rendered: ' + " + jsonSelector + "); element.click(); })()", cancellationToken);
    }

    private static string DescribeHumanReviewResponseLoss(int status, string body)
    {
        var bodyDescription = "non-json";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            bodyDescription = DescribeHumanReviewResponseLossBody(root);
        }
        catch (JsonException)
        {
        }

        return $"http={status}; body-length={body.Length}; {bodyDescription}";
    }

    private static string DescribeHumanReviewResponseLossBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String)
        {
            return "json-status=invalid; detail=unavailable";
        }

        var responseStatus = status.GetString() ?? "null";
        if (!root.TryGetProperty("detail", out var detail)
            || detail.ValueKind != JsonValueKind.Object
            || !detail.TryGetProperty("summary", out var summary)
            || summary.ValueKind != JsonValueKind.Object
            || !summary.TryGetProperty("lifecycleStatus", out var lifecycle)
            || lifecycle.ValueKind != JsonValueKind.String
            || !summary.TryGetProperty("lifecycleVersion", out var lifecycleVersion)
            || lifecycleVersion.ValueKind != JsonValueKind.Number
            || !detail.TryGetProperty("decisions", out var decisions)
            || decisions.ValueKind != JsonValueKind.Array
            || !detail.TryGetProperty("runtime", out var runtime)
            || runtime.ValueKind != JsonValueKind.Object
            || !runtime.TryGetProperty("continuationStatus", out var continuation)
            || continuation.ValueKind != JsonValueKind.String)
        {
            return $"json-status={responseStatus}; detail=unavailable";
        }

        return $"json-status={responseStatus}; lifecycle={lifecycle.GetString() ?? "null"}; version={lifecycleVersion.GetRawText()}; decisions={decisions.GetArrayLength()}; continuation={continuation.GetString() ?? "null"}";
    }

    private static void AssertHumanReviewIdentity(JsonDocument document, string runId, string requestId, string requestHash, long lifecycleVersion)
    {
        var summary = document.RootElement.GetProperty("detail").GetProperty("summary");
        Assert.Equal("ready", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(runId, summary.GetProperty("runId").GetString());
        Assert.Equal(requestId, summary.GetProperty("requestId").GetString());
        Assert.Equal(requestHash, summary.GetProperty("requestHash").GetString());
        Assert.Equal(lifecycleVersion, summary.GetProperty("lifecycleVersion").GetInt64());
    }

    private static async Task AssertValueFreeHumanReviewOperationStorageAsync(HeadlessBrowserSession browser, string operationId, long lifecycleVersion, string requestId, string requestHash, string runId)
    {
        var stored = await browser.EvaluateStringAsync("JSON.stringify(Array.from({ length: localStorage.length }, (_, index) => localStorage.key(index)).filter(key => key?.startsWith('embodysense.human-review.operations.v1.')).map(key => ({ key, value: localStorage.getItem(key) })))");
        using var document = JsonDocument.Parse(stored);
        var entries = document.RootElement;
        Assert.Equal(JsonValueKind.Array, entries.ValueKind);
        var storage = Assert.Single(entries.EnumerateArray());
        Assert.False(string.IsNullOrWhiteSpace(storage.GetProperty("key").GetString()));
        var raw = storage.GetProperty("value").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raw));
        using var storedDocument = JsonDocument.Parse(raw!);
        var root = storedDocument.RootElement;
        Assert.Equal(new[] { "entries", "schemaVersion", "scope" }, root.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        var storedEntry = Assert.Single(root.GetProperty("entries").EnumerateArray());
        Assert.Equal(new[] { "action", "expectedLifecycleVersion", "operationId", "requestHash", "requestId", "runId" }, storedEntry.EnumerateObject().Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal("approve", storedEntry.GetProperty("action").GetString());
        Assert.Equal(operationId, storedEntry.GetProperty("operationId").GetString());
        Assert.Equal(lifecycleVersion, storedEntry.GetProperty("expectedLifecycleVersion").GetInt64());
        Assert.Equal(requestId, storedEntry.GetProperty("requestId").GetString());
        Assert.Equal(requestHash, storedEntry.GetProperty("requestHash").GetString());
        Assert.Equal(runId, storedEntry.GetProperty("runId").GetString());
        var valueFreeEntry = storedEntry.GetRawText();
        foreach (var forbidden in new[] { "actor", "role", "grant", "authority", "effect", "credential", "private", "secret", "detail" })
            Assert.DoesNotContain(forbidden, valueFreeEntry, StringComparison.OrdinalIgnoreCase);
    }

    private static Task AssertHumanReviewResponseLossBrowserHealthyAsync(HeadlessBrowserSession browser, string runId)
    {
        var runFragment = $"/api/human-reviews/{Uri.EscapeDataString(runId)}";
        var observedUnavailable = browser.DiagnosticsSnapshot().Any(item => item.Contains(runFragment, StringComparison.Ordinal)
            && (item.Contains("\"status\":503", StringComparison.Ordinal) || item.Contains("status of 503 (", StringComparison.Ordinal)));
        return observedUnavailable
            ? browser.AssertHealthyAsync((runFragment, 503))
            : browser.AssertHealthyAsync();
    }

    private static async Task<CustomLoopRunRecord> WaitForCompletedHumanReviewResponseLossAsync(WorkspacePaths paths, string runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var store = new CustomLoopRunStore(paths);
        var lastStatus = "missing";
        while (!timeout.IsCancellationRequested)
        {
            CustomLoopRunRecord? run;
            try
            {
                run = await store.GetAsync(runId, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }

            lastStatus = run is null ? "missing" : $"status={run.Status}; lifecycle={run.LifecycleVersion}";
            if (run?.Status == CustomLoopRunStatus.Completed)
                return run;

            try
            {
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Human Review run `{runId}` did not complete after the response-loss retry ({lastStatus}).");
    }

    private static async Task AssertHumanReviewStillBlockedBeforeRecoveryAsync(WorkspacePaths paths, string runId)
    {
        using var store = new CustomLoopRunStore(paths);
        var durable = await store.GetAsync(runId).ConfigureAwait(false) ?? throw new InvalidOperationException("The response-loss Human Review run disappeared before recovery.");
        Assert.Equal(CustomLoopRunStatus.Paused, durable.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, durable.Frontier?.Payload.Status);
        var review = Assert.IsType<HumanReviewRunState>(durable.HumanReview);
        Assert.NotNull(review.ContinuationReservation);
        Assert.Null(review.Continuation);
        var effect = Assert.IsType<HumanReviewEffectAttemptBinding>(review.Request.Binding.EffectAttempt);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var read = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(workspaceId, effect.OperationId, effect.EffectGeneration).ConfigureAwait(false);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, read.Status);
        var attempt = Assert.IsType<GovernedLoopEffectAttempt>(read.Attempt);
        Assert.Contains(attempt.Payload.Phase, new[] { GovernedLoopEffectPhase.IntentPrepared, GovernedLoopEffectPhase.DispatchNotStarted });
        Assert.DoesNotContain(durable.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
    }

    private static async Task AssertSingleApprovedPreDispatchEffectAsync(WorkspacePaths paths, CustomLoopRunRecord durable)
    {
        var validation = CustomLoopRunValidator.Validate(durable);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        var review = Assert.IsType<HumanReviewRunState>(durable.HumanReview);
        var continuation = Assert.IsType<HumanReviewContinuationState>(review.Continuation);
        var completion = Assert.IsType<HumanReviewContinuationCompletion>(continuation.Completion);
        Assert.Equal(HumanReviewContinuationReleaseKind.PreDispatchEffect, completion.ReleaseReceipt.Kind);
        Assert.Equal(HumanReviewContinuationReleaseDisposition.Released, completion.ReleaseReceipt.Disposition);
        Assert.Single(durable.Events, item => item.EventId == completion.ReleaseReceipt.ReleaseOperationId);
        var effect = Assert.IsType<HumanReviewEffectAttemptBinding>(review.Request.Binding.EffectAttempt);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var read = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(workspaceId, effect.OperationId, effect.EffectGeneration).ConfigureAwait(false);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, read.Status);
        Assert.Equal(GovernedLoopEffectPhase.Committed, Assert.IsType<GovernedLoopEffectAttempt>(read.Attempt).Payload.Phase);
        Assert.Single(durable.Events, item => item.Kind == CustomLoopRunEventKind.NodeAttemptCompleted);
    }

    private sealed partial class HeadlessBrowserSession
    {
        public async Task EvaluateWithUserGestureAsync(string expression, CancellationToken cancellationToken)
        {
            _ = await EvaluateAsync(expression, cancellationToken, userGesture: true);
        }
    }

}
