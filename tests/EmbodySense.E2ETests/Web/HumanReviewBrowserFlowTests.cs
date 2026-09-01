using System.Text.Json;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    private const string HumanReviewBrowserProfileId = "org.example/model-profile/human-review";

    [InstalledBrowserFact]
    public async Task Human_review_browser_exposes_redacted_detail_and_all_four_visible_decisions()
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
        var runIds = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["request-information"] = "browser-human-review-request-information",
            ["reject"] = "browser-human-review-reject",
            ["cancel"] = "browser-human-review-cancel",
            ["approve"] = "browser-human-review-approve",
        };
        foreach (var action in runIds)
        {
            await HumanReviewBrowserFixture.SeedPendingAsync(paths, action.Value, "bounded browser review " + action.Key, capabilityTrustRoot: capabilityTrustRoot);
        }
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await browser.WaitForExpressionAsync($"document.querySelectorAll('[data-testid=\\\"human-review-item\\\"]').length === 4");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelectorAll('[data-testid=\"human-review-item\"]').length <= 50"));
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('loopApprovalPanel')?.hidden === true && document.getElementById('loopApprovalPanel')?.dataset.legacyNonAuthoritative === 'true' && document.getElementById('loopApprovals')?.children.length === 0"));
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('humanReviewSummary').querySelectorAll('dt').length <= 12"));
            Assert.False(await browser.EvaluateBooleanAsync("document.body.innerHTML.includes('access_token') || document.body.innerHTML.includes('grantReference') || document.body.innerHTML.includes('eligibleRespondents')"));

            await SelectHumanReviewAsync(browser, runIds["request-information"]);
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread')");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-approve\"]')?.getAttribute('aria-disabled') === 'false'"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-reject\"]')?.getAttribute('aria-disabled') === 'false'"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-cancel\"]')?.getAttribute('aria-disabled') === 'false'"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-request-information\"]')?.getAttribute('aria-disabled') === 'false'"));
            await ClickAsync(browser, "[data-testid=\"human-review-request-information\"]");
            await browser.WaitForExpressionAsync("!document.getElementById('humanReviewInformationField').hidden");
            await SetValueAsync(browser, "#humanReviewInformationDetail", "Please provide one bounded confirmation.");
            await ClickAsync(browser, "[data-testid=\"human-review-request-information\"]");
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewActionStatus').textContent.toLowerCase().includes('information request was recorded') && document.getElementById('humanReviewEvidence').textContent.toLowerCase().includes('information requested')");
            Assert.Contains("awaiting information", (await browser.EvaluateStringAsync("document.getElementById('humanReviewLifecycleStatus').textContent")).ToLowerInvariant(), StringComparison.Ordinal);
            Assert.Contains(runIds["request-information"], await ReadHumanReviewAsync(browser, runIds["request-information"]), StringComparison.Ordinal);
            await AssertNoReviewDispatchAsync(browser, runIds["request-information"]);

            await SelectHumanReviewAsync(browser, runIds["reject"]);
            await ClickAsync(browser, "[data-testid=\"human-review-reject\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "rejected");
            await AssertNoReviewDispatchAsync(browser, runIds["reject"]);

            await SelectHumanReviewAsync(browser, runIds["cancel"]);
            await ClickAsync(browser, "[data-testid=\"human-review-cancel\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "cancelled");
            await AssertNoReviewDispatchAsync(browser, runIds["cancel"]);

            await SelectHumanReviewAsync(browser, runIds["approve"]);
            await browser.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-approve\\\"]').focus()");
            Assert.True(await browser.EvaluateBooleanAsync("document.activeElement?.getAttribute('data-testid') === 'human-review-approve'"));
            await browser.PressKeyAsync("Enter");
            await WaitForCanonicalHumanReviewAsync(browser, "approved", 1);
            var approved = await ReadHumanReviewAsync(browser, runIds["approve"]);
            Assert.Contains("approve", approved, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await browser.EvaluateInt32Async("document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelectorAll('#humanReviewDetailPanel script').length === 0 && !document.getElementById('humanReviewDetailPanel').innerHTML.toLowerCase().includes('<script')"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-detail-refresh\"]')?.getAttribute('aria-label') === null || document.querySelector('[data-testid=\"human-review-detail-refresh\"]')?.textContent.trim().length > 0"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_exposes_redacted_detail_and_all_four_visible_decisions), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_review_browser_rereads_after_refresh_and_process_restart_before_approving_once()
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
        var runId = "browser-human-review-restart";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "restart durable review", capabilityTrustRoot: capabilityTrustRoot);
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        HeadlessBrowserSession? browser = null;
        string? retiredServerOutput = null;
        try
        {
            browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
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
            browser.EndExpectedServerRestart();
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread')");
            await ClickAsync(browser, "[data-testid=\"human-review-approve\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "approved");
            var first = await ReadHumanReviewAsync(browser, runId);
            Assert.Equal(1, await browser.EvaluateInt32Async("document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length"));
            Assert.Contains("approved", first, StringComparison.OrdinalIgnoreCase);
            AssertCanonicalApproval(first);
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await WaitForHumanReviewLifecycleAsync(browser, "approved");
            var reread = await ReadHumanReviewAsync(browser, runId);
            Assert.Equal(1, await browser.EvaluateInt32Async("document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length"));
            Assert.Contains("approved", reread, StringComparison.OrdinalIgnoreCase);
            AssertCanonicalApproval(reread);
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_rereads_after_refresh_and_process_restart_before_approving_once), browser, app, retiredServerOutput);
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
    public async Task Human_review_browser_uses_two_same_profile_tabs_for_one_exact_decision_operation()
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
        var runId = "browser-human-review-two-tabs";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "two tab decision", capabilityTrustRoot: capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await using var tab = await browser.OpenTabAsync(app.BaseUrl);
            await tab.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-nav\\\"]').click()");
            await tab.WaitForExpressionAsync("document.querySelector('[data-testid=\\\"human-review-item\\\"]') !== null");
            await tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-item\\\"]').click()");
            await tab.WaitForExpressionAsync("document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread')");
            var parentClick = browser.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-approve\\\"]').click()");
            var childClick = tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-approve\\\"]').click()");
            await Task.WhenAll(parentClick, childClick);
            await browser.WaitForExpressionAsync("/approval was recorded|already recorded/i.test(document.getElementById('humanReviewActionStatus').textContent)");
            await tab.WaitForExpressionAsync("/approval was recorded|already recorded/i.test(document.getElementById('humanReviewActionStatus').textContent)");
            await WaitForCanonicalHumanReviewAsync(browser, "approved", 1);
            await WaitForCanonicalHumanReviewAsync(tab, "approved", 1);
            await tab.ReloadAsync();
            await tab.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-nav\\\"]').click()");
            await tab.WaitForExpressionAsync("document.querySelector('[data-testid=\\\"human-review-item\\\"]') !== null");
            await tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\\\"human-review-item\\\"]').click()");
            await tab.WaitForExpressionAsync("document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread')");
            await WaitForCanonicalHumanReviewAsync(tab, "approved", 1, refresh: false);
            var review = await ReadHumanReviewAsync(browser, runId);
            Assert.Contains("approve", review, StringComparison.OrdinalIgnoreCase);
            AssertCanonicalApproval(review);
            Assert.Equal(1, await browser.EvaluateInt32Async("document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length"));
            Assert.Equal(1, await tab.EvaluateInt32Async("document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length"));
            Assert.True(await tab.EvaluateBooleanAsync("document.querySelectorAll('[data-testid=\\\"human-review-item\\\"]').length <= 50"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_uses_two_same_profile_tabs_for_one_exact_decision_operation), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_review_browser_rejects_anonymous_decisions_and_keeps_authority_out_of_the_projection()
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
        var runId = "browser-human-review-security";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "<script>secret-canary</script>", capabilityTrustRoot: capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            var anonymousStatus = await browser.EvaluateInt32Async($"(async () => {{ const response = await fetch('/api/human-reviews/{Uri.EscapeDataString(runId)}/approve', {{ method: 'POST', credentials: 'omit', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify({{ expectedLifecycleVersion: 1, operationId: 'anonymous-operation' }}) }}); return response.status; }})()");
            Assert.Equal(401, anonymousStatus);
            var forgedStatus = await browser.EvaluateInt32Async($"(async () => {{ const response = await fetch('/api/human-reviews/{Uri.EscapeDataString(runId)}/reject', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify({{ expectedLifecycleVersion: 999, operationId: 'forged-operation', actor: 'forged', role: 'admin', scope: 'all' }}) }}); return response.status; }})()");
            Assert.Equal(400, forgedStatus);
            var body = await browser.EvaluateStringAsync("document.body.textContent");
            Assert.DoesNotContain("secret-canary", body, StringComparison.Ordinal);
            Assert.DoesNotContain("grantReference", body, StringComparison.Ordinal);
            Assert.DoesNotContain("eligibleRespondents", body, StringComparison.Ordinal);
            Assert.False(await browser.EvaluateBooleanAsync("document.body.innerHTML.includes('<script>')"));
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\\\"human-review-nav\\\"]')?.getAttribute('aria-controls') === 'humanReviewView'"));
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('humanReviewActionStatus').getAttribute('aria-live') === 'assertive'"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync(
                ($"/api/human-reviews/{runId}/approve", 401),
                ($"/api/human-reviews/{runId}/reject", 400));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_rejects_anonymous_decisions_and_keeps_authority_out_of_the_projection), browser, app);
            throw;
        }
    }

    private static BrowserModelProfileSpec HumanReviewBrowserProfile()
        => new(HumanReviewBrowserProfileId, "human-review", "Test-only Human Review browser model profile.", "gpt-test", true);

    private static async Task SeedHumanReviewReadinessAuthorityAsync(WorkspacePaths paths, string capabilityTrustRoot)
    {
        Assert.True(AuthorityProfileId.TryParse("human-review-browser", out var profileId, out var profileIdError), profileIdError?.ToString());
        Assert.True(AuthorityProfileRevision.TryParse("1", out var revision, out var revisionError), revisionError?.ToString());
        Assert.True(AuthorityPurpose.TryParse("human-review-browser-e2e", out var purpose, out var purposeError), purposeError?.ToString());
        Assert.True(AuthorityActorId.TryParse("browser-e2e", out var actor, out var actorError), actorError?.ToString());
        var profile = new AuthorityProfile(
            AuthorityProfile.CurrentSchemaVersion,
            profileId!,
            revision!,
            AuthorityProfileStatus.Active,
            purpose!,
            new AuthorityProvenance(actor!, AuthorityProvenanceKind.UserDeclaration),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            null,
            new AuthorityCeiling([], [], 0, CapabilitySideEffectClass.None, false, false, false),
            []);
        var store = new AuthorityProfileStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var result = await store.MutateAsync(new AuthorityProfileMutation(
            AuthorityProfileMutationKind.Create,
            "initialize-human-review-browser-authority",
            0,
            profile,
            null,
            null,
            actor!,
            purpose!));
        Assert.Equal(AuthorityProfileMutationStatus.Applied, result.Status);
    }

    private static string BrowserCapabilityTrustRoot(string serverAccountRoot)
    {
        var localApplicationData = OperatingSystem.IsMacOS()
            ? Path.Combine(serverAccountRoot, "Library", "Application Support")
            : Path.Combine(serverAccountRoot, "local-data");
        return Path.Combine(localApplicationData, "EmbodySense", "server-state", "capability-catalog");
    }

    private static async Task OpenHumanReviewAsync(HeadlessBrowserSession browser)
    {
        await ClickAsync(browser, "[data-testid=\"human-review-nav\"]");
        await browser.WaitForExpressionAsync("!document.getElementById('humanReviewView').hidden && document.getElementById('humanReviewListStatus').textContent.length > 0");
    }

    private static async Task SelectHumanReviewAsync(HeadlessBrowserSession browser, string runId)
    {
        var selector = JsonSerializer.Serialize($"[data-testid=\"human-review-item\"][data-run-id=\"{runId}\"]");
        await browser.EvaluateWithUserGestureAsync($"(() => {{ const item = document.querySelector({selector}); if (!item) throw new Error('Human Review item was not rendered: ' + {JsonSerializer.Serialize(runId)}); item.click(); }})()");
        await browser.WaitForExpressionAsync("document.getElementById('humanReviewDetailPanel').hidden === false && document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread')");
    }

    private static async Task WaitForHumanReviewLifecycleAsync(HeadlessBrowserSession browser, string lifecycle)
    {
        var token = JsonSerializer.Serialize(lifecycle.Replace('-', ' '));
        await browser.WaitForExpressionAsync($"document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes({token})");
    }

    private static async Task WaitForCanonicalHumanReviewAsync(HeadlessBrowserSession browser, string lifecycle, int decisionCount, bool refresh = true)
    {
        var lifecycleToken = JsonSerializer.Serialize(lifecycle.Replace('-', ' '));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await browser.EvaluateBooleanAsync($"document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread') && document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes({lifecycleToken}) && document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length === {decisionCount}", timeout.Token).ConfigureAwait(false))
                return;

            if (refresh && await browser.EvaluateBooleanAsync("document.getElementById('humanReviewDetailStatus').textContent.toLowerCase().includes('invalid') || document.getElementById('humanReviewDetailStatus').textContent.toLowerCase().includes('failed')", timeout.Token).ConfigureAwait(false))
                await ClickAsync(browser, "[data-testid=\"human-review-detail-refresh\"]").ConfigureAwait(false);

            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Canonical Human Review {lifecycle} state did not become visible with {decisionCount} decision(s).");
    }

    private static async Task WaitForCanonicalHumanReviewAsync(HeadlessBrowserTab tab, string lifecycle, int decisionCount, bool refresh = true)
    {
        var lifecycleToken = JsonSerializer.Serialize(lifecycle.Replace('-', ' '));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            if (await tab.EvaluateBooleanAsync($"document.getElementById('humanReviewDetailStatus').textContent.includes('Canonical state reread') && document.getElementById('humanReviewLifecycleStatus').textContent.toLowerCase().includes({lifecycleToken}) && document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length === {decisionCount}", timeout.Token).ConfigureAwait(false))
                return;

            if (refresh && await tab.EvaluateBooleanAsync("document.getElementById('humanReviewDetailStatus').textContent.toLowerCase().includes('invalid') || document.getElementById('humanReviewDetailStatus').textContent.toLowerCase().includes('failed')", timeout.Token).ConfigureAwait(false))
                await tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-review-detail-refresh\"]').click()").ConfigureAwait(false);

            await Task.Delay(100, timeout.Token).ConfigureAwait(false);
        }

        throw new TimeoutException($"Canonical Human Review {lifecycle} state did not become visible with {decisionCount} decision(s).");
    }

    private static Task<string> ReadHumanReviewAsync(HeadlessBrowserSession browser, string runId)
    {
        var route = JsonSerializer.Serialize($"/api/human-reviews/{Uri.EscapeDataString(runId)}");
        return browser.EvaluateStringAsync($"(async () => {{ const response = await fetch({route}, {{ cache: 'no-store' }}); if (!response.ok) throw new Error('Human Review read failed: ' + response.status); return JSON.stringify(await response.json()); }})()");
    }

    private static void AssertCanonicalApproval(string serializedReview)
    {
        using var document = JsonDocument.Parse(serializedReview);
        var root = document.RootElement;
        Assert.Equal("ready", root.GetProperty("status").GetString(), ignoreCase: true);
        var decisions = root.GetProperty("detail").GetProperty("decisions");
        var decision = Assert.Single(decisions.EnumerateArray());
        Assert.Equal("approve", decision.GetProperty("kind").GetString(), ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("operationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(decision.GetProperty("decisionHash").GetString()));
    }

    private static async Task AssertNoReviewDispatchAsync(HeadlessBrowserSession browser, string runId)
    {
        var run = await ReadRunFromBrowserAsync(browser, runId);
        Assert.DoesNotContain("dispatched", run, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conclusive", run, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task InitializeWorkspaceAsyncIfNeededAsync(HeadlessBrowserSession browser)
    {
        var status = await browser.EvaluateStringAsync("document.getElementById('workspaceStatus')?.textContent ?? ''");
        if (status.Contains("Needs initialization", StringComparison.Ordinal))
        {
            await InitializeWorkspaceAsync(browser);
        }
        else
        {
            await browser.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized')");
            await browser.WaitForExpressionAsync("document.getElementById('configContent').textContent.includes('compatible-test')");
        }
    }

}
