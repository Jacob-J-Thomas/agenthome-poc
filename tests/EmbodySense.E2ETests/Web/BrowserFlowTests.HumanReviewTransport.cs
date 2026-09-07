using System.Text.Json;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;
using EmbodySense.Web;
using EmbodySense.Web.Services;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    [InstalledBrowserFact]
    public async Task Human_review_browser_retries_a_pre_send_disconnect_with_the_same_operation_identity()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanReviewBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        var runId = "browser-human-review-pre-send-loss";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "pre-send transport loss", capabilityTrustRoot: capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            var actionPath = $"/api/human-reviews/{Uri.EscapeDataString(runId)}/approve";
            await browser.EvaluateAsync(HumanReviewBrowserTransportScripts.InstallPreSendFailure(actionPath));
            await browser.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-review-approve\"]').focus()");
            Assert.True(await browser.EvaluateBooleanAsync("document.activeElement?.getAttribute('data-testid') === 'human-review-approve'"));
            await browser.PressKeyAsync("Enter");
            await browser.WaitForExpressionAsync("window.__humanReviewTransport?.mode === 'pre-send-failed' && window.__humanReviewTransport.attempts === 1");
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewActionStatus').textContent.toLowerCase().includes('temporarily unavailable')");
            await WaitForHumanReviewLifecycleAsync(browser, "pending");
            await AssertNoReviewDispatchAsync(browser, runId);
            Assert.Equal(0, await browser.EvaluateInt32Async("window.__humanReviewTransport.networkPosts"));
            await browser.WaitForExpressionAsync("document.querySelector('[data-testid=\"human-review-approve\"]')?.disabled === false");

            await ClickAsync(browser, "[data-testid=\"human-review-approve\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "approved");
            Assert.Equal(2, await browser.EvaluateInt32Async("window.__humanReviewTransport.attempts"));
            Assert.Equal(1, await browser.EvaluateInt32Async("window.__humanReviewTransport.networkPosts"));
            Assert.True(await browser.EvaluateBooleanAsync("window.__humanReviewTransport.statuses.length === 1 && window.__humanReviewTransport.statuses[0] === 200"));
            var payloads = await ReadTransportPayloadsAsync(browser);
            Assert.Equal(2, payloads.Length);
            Assert.Equal(ReadOperationId(payloads[0]), ReadOperationId(payloads[1]));
            var review = await ReadHumanReviewAsync(browser, runId);
            AssertCanonicalApproval(review);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_retries_a_pre_send_disconnect_with_the_same_operation_identity), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_review_browser_keeps_a_stale_same_profile_tab_at_409_and_activates_the_winner_by_keyboard()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanReviewBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        var runId = "browser-human-review-stale-tab";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "stale same-profile tab", capabilityTrustRoot: capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await using var staleTab = await browser.OpenTabAsync(app.BaseUrl);
            await staleTab.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await staleTab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-review-nav\"]').click()");
            await staleTab.WaitForExpressionAsync("document.querySelector('[data-testid=\"human-review-item\"]') !== null");
            await staleTab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-review-item\"]').click()");
            await staleTab.WaitForExpressionAsync("document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread')");
            var actionPath = $"/api/human-reviews/{Uri.EscapeDataString(runId)}/approve";
            await staleTab.EvaluateWithUserGestureAsync(HumanReviewBrowserTransportScripts.InstallStaleReadConflict(actionPath, runId));
            Assert.True(await staleTab.EvaluateBooleanAsync("window.__humanReviewTransport?.snapshotsReady === true && document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes('pending')"));
            Assert.True(await staleTab.EvaluateBooleanAsync("(() => { const button = document.querySelector('[data-testid=\"human-review-approve\"]'); return button?.type === 'button' && button?.getAttribute('aria-disabled') === 'false'; })()"));

            await ClickAsync(browser, "[data-testid=\"human-review-reject\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "rejected");
            Assert.True(await staleTab.EvaluateBooleanAsync("document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes('pending')"));
            await staleTab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-review-approve\"]').focus()");
            Assert.True(await staleTab.EvaluateBooleanAsync("document.activeElement?.getAttribute('data-testid') === 'human-review-approve'"));
            await staleTab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-review-approve\"]').click()");
            await staleTab.WaitForExpressionAsync("window.__humanReviewTransport.mode === 'off' && window.__humanReviewTransport.statuses.length === 1 && window.__humanReviewTransport.statuses[0] === 409");
            await staleTab.WaitForExpressionAsync("window.__humanReviewTransport.conflictFeedbackObserved === true");
            await staleTab.WaitForExpressionAsync("document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes('rejected')");
            await AssertNoReviewDispatchAsync(browser, runId);
            var review = await ReadHumanReviewAsync(browser, runId);
            using var document = JsonDocument.Parse(review);
            Assert.Equal("reject", document.RootElement.GetProperty("detail").GetProperty("decisions")[0].GetProperty("kind").GetString(), ignoreCase: true);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
            Assert.True(await staleTab.EvaluateBooleanAsync("document.querySelectorAll('[data-testid=\"human-review-item\"]').length <= 50"));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_keeps_a_stale_same_profile_tab_at_409_and_activates_the_winner_by_keyboard), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_review_browser_rejects_an_old_session_cookie_after_real_process_restart()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanReviewBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        var runId = "browser-human-review-stale-session";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "stale session token", capabilityTrustRoot: capabilityTrustRoot);
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        HeadlessBrowserSession? browser = null;
        try
        {
            browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            var cookieName = WebSessionSecurity.GetCookieName(port);
            var oldCookie = await browser.ReadCookieValueAsync(cookieName);
            Assert.False(string.IsNullOrWhiteSpace(oldCookie));
            await browser.BeginExpectedServerRestartAsync();
            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent === 'Web primary'");
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await browser.EndExpectedServerRestartAsync();
            var replacementCookie = await browser.ReadCookieValueAsync(cookieName);
            Assert.False(string.IsNullOrWhiteSpace(replacementCookie));
            Assert.NotEqual(oldCookie, replacementCookie);
            await browser.SetCookieValueAsync(cookieName, oldCookie!, app.BaseUrl);
            var competingRecoveryStatus = await browser.EvaluateInt32Async($"(async () => {{ try {{ await window.embodySenseSession.requestJson('/api/human-reviews/{Uri.EscapeDataString(runId)}', {{ cache: 'no-store' }}); return 0; }} catch (error) {{ return Number(error?.status ?? -1); }} }})()");
            Assert.Equal(401, competingRecoveryStatus);
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent === 'Web primary'");
            Assert.Equal(replacementCookie, await browser.ReadCookieValueAsync(cookieName));
            await browser.EvaluateAsync($"location.href = URL.createObjectURL(new Blob(['<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"icon\" href=\"{app.BaseUrl}/favicon.svg\"><title>Credential probe</title></head><body></body></html>'], {{ type: 'text/html' }}))");
            await browser.WaitForExpressionAsync("location.protocol === 'blob:' && document.title === 'Credential probe' && document.readyState === 'complete'");
            await browser.SetCookieValueAsync(cookieName, oldCookie!, app.BaseUrl);
            Assert.Equal(oldCookie, await browser.ReadCookieValueAsync(cookieName));
            var staleStatus = await browser.EvaluateInt32Async($"(async () => {{ const response = await fetch('{app.BaseUrl}/api/human-reviews/{Uri.EscapeDataString(runId)}', {{ cache: 'no-store', credentials: 'same-origin' }}); return response.status; }})()");
            Assert.Equal(401, staleStatus);
            Assert.Equal(oldCookie, await browser.ReadCookieValueAsync(cookieName));
            await browser.SetCookieValueAsync(cookieName, replacementCookie!, app.BaseUrl);
            Assert.Equal(replacementCookie, await browser.ReadCookieValueAsync(cookieName));
            await browser.NavigateAsync(app.BaseUrl);
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await WaitForHumanReviewLifecycleAsync(browser, "pending");
            await AssertNoReviewDispatchAsync(browser, runId);
            app.AssertHealthy();
            await browser.AssertHealthyAsync(($"/api/human-reviews/{runId}", 401));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_rejects_an_old_session_cookie_after_real_process_restart), browser, app);
            throw;
        }
        finally
        {
            if (browser is not null)
                await browser.DisposeAsync();
            if (app is not null)
                await app.DisposeAsync();
        }
    }

    private static async Task<string[]> ReadTransportPayloadsAsync(HeadlessBrowserSession browser)
    {
        var serialized = await browser.EvaluateStringAsync("JSON.stringify(window.__humanReviewTransport.payloads)");
        return JsonSerializer.Deserialize<string[]>(serialized) ?? [];
    }

    private static string ReadOperationId(string serializedPayload)
    {
        using var document = JsonDocument.Parse(serializedPayload);
        return document.RootElement.GetProperty("operationId").GetString() ?? throw new InvalidOperationException("The browser decision payload did not include an operation identity.");
    }
}
