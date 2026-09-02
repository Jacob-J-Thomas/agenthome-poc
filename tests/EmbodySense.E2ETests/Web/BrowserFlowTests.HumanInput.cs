using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    private const string HumanInputBrowserProfileId = "org.example/model-profile/human-input";

    [InstalledBrowserFact]
    public async Task Human_input_browser_recovers_a_committed_response_loss_by_canonical_reread_once()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanInputBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        const string RequestId = "browser-human-input-response-loss";
        await HumanInputBrowserFixture.SeedWaitingAsync(paths, RequestId, "Collect the exact response after a committed transport loss.", capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            await SetValueAsync(browser, "#humanInputResponseEditor textarea", "one exact browser answer");
            var actionPath = $"/api/human-input/{Uri.EscapeDataString(RequestId)}/answer";
            await browser.EvaluateAsync(HumanInputBrowserTransportScripts.InstallPostCommitResponseLoss(actionPath));
            await ClickAsync(browser, "[data-testid=\"human-input-response-submit\"]");
            await browser.WaitForExpressionAsync("window.__humanInputResponseLoss?.mode === 'post-commit-lost' && window.__humanInputResponseLoss.attempts === 1 && window.__humanInputResponseLoss.statuses.length === 1 && window.__humanInputResponseLoss.statuses[0] === 200");
            await browser.WaitForExpressionAsync("document.getElementById('humanInputResponseStatus').textContent.toLowerCase().includes('temporarily unavailable')");
            await WaitForHumanInputLifecycleAsync(browser, "answered");
            await browser.WaitForExpressionAsync("document.querySelector('[data-testid=\"human-input-response-submit\"]')?.disabled === true && document.querySelector('#humanInputResponseEditor textarea')?.value === ''");

            var payload = await ReadHumanInputPayloadAsync(browser, "window.__humanInputResponseLoss.payloads[0]");
            AssertBrowserResponsePayload(payload, RequestId);
            Assert.Equal(1, await browser.EvaluateInt32Async("window.__humanInputResponseLoss.attempts"));
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).some(key => key.toLowerCase().includes('human-input') || key.toLowerCase().includes('operation'))"));

            var responses = await HumanInputBrowserFixture.ReadResponsesAsync(paths, capabilityTrustRoot, RequestId);
            var responseSnapshot = Assert.IsType<HumanInputResponseLifecycleStoreSnapshot>(responses.Snapshot);
            var response = Assert.Single(responseSnapshot.Responses);
            var operation = Assert.Single(responseSnapshot.Operations);
            Assert.Equal(payload.GetProperty("responseId").GetString(), response.ResponseId);
            Assert.Equal(payload.GetProperty("operationId").GetString(), operation.OperationId);
            Assert.Equal(HumanInputResponseOperationOutcome.Committed, operation.Outcome);
            Assert.Equal(response.ResponseId, operation.SubmittedResponse?.ResponseId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Answered, operation.ResultHead?.Status);
            var run = await WaitForHumanInputContinuationAsync(paths, RequestId + "-run");
            Assert.Equal(CustomLoopRunStatus.Completed, run.Status);
            Assert.Equal(GovernedLoopFrontierStatus.Completed, run.Frontier?.Payload.Status);
            Assert.Single(run.HumanInputWaitingCheckpoints);
            Assert.Equal(GovernedLoopHumanInputWaitingCheckpointPosture.Terminal, run.HumanInputWaitingCheckpoints[0].Posture);

            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            await WaitForHumanInputLifecycleAsync(browser, "answered");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-input-response-submit\"]')?.disabled === true && document.querySelector('#humanInputResponseEditor textarea')?.value === ''"));
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).some(key => key.toLowerCase().includes('human-input') || key.toLowerCase().includes('operation'))"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_input_browser_recovers_a_committed_response_loss_by_canonical_reread_once), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_input_browser_uses_two_same_profile_tabs_for_concurrent_valid_response_winner_conflict_and_accessibility()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanInputBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        const string RequestId = "browser-human-input-stale-tab";
        await HumanInputBrowserFixture.SeedPendingAsync(paths, RequestId, "Stale same-profile Human Input request.", capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            await using var staleTab = await browser.OpenTabAsync(app.BaseUrl);
            await InitializeWorkspaceInTabAsync(staleTab);
            await OpenHumanInputAsync(staleTab);
            await SelectHumanInputAsync(staleTab, RequestId);
            Assert.True(await staleTab.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-input-item\"]')?.getAttribute('aria-selected') === 'true' && document.querySelector('#humanInputResponseEditor textarea')?.tagName === 'TEXTAREA' && document.getElementById('humanInputResponseStatus')?.getAttribute('aria-live') === 'assertive'"));
            Assert.True(await browser.EvaluateBooleanAsync("!document.getElementById('humanInputView').hidden && document.getElementById('loopApprovalPanel')?.hidden === true && !document.querySelector('[data-human-input-action=\"approve\"]')"));

            await browser.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-input-refresh\"]')?.focus()");
            Assert.True(await browser.EvaluateBooleanAsync("document.activeElement?.getAttribute('data-testid') === 'human-input-refresh'"));
            await browser.PressKeyAsync("Enter");
            await browser.WaitForExpressionAsync("document.getElementById('humanInputRefreshButton')?.disabled === false && document.getElementById('humanInputListStatus')?.textContent?.includes('durable data request') && document.getElementById('humanInputLifecycleStatus')?.textContent?.toLowerCase().includes('pending') && document.querySelector('[data-testid=\"human-input-response-submit\"]')?.disabled === false");

            await SetValueAsync(browser, "#humanInputResponseEditor textarea", "answer from the first tab");
            await SetValueForTabAsync(staleTab, "#humanInputResponseEditor textarea", "answer from the second tab");
            var actionPath = $"/api/human-input/{Uri.EscapeDataString(RequestId)}/answer";
            await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(actionPath));
            await staleTab.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(actionPath));
            await Task.WhenAll(
                browser.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-input-response-submit\"]')?.click()"),
                staleTab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-input-response-submit\"]')?.click()"));
            await Task.WhenAll(
                browser.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1"),
                staleTab.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1"));
            var firstTabStatus = await browser.EvaluateInt32Async("window.__humanInputPostCapture.statuses[0]");
            var secondTabStatus = await staleTab.EvaluateInt32Async("window.__humanInputPostCapture.statuses[0]");
            Assert.True((firstTabStatus == 200 && secondTabStatus == 409) || (firstTabStatus == 409 && secondTabStatus == 200), $"Expected one accepted and one conflicting response, got {firstTabStatus} and {secondTabStatus}.");
            var winnerPayload = firstTabStatus == 200
                ? await browser.EvaluateStringAsync("window.__humanInputPostCapture.payloads[0]")
                : await staleTab.EvaluateStringAsync("window.__humanInputPostCapture.payloads[0]");
            using var winnerPayloadDocument = JsonDocument.Parse(winnerPayload);
            var winnerOperationId = winnerPayloadDocument.RootElement.GetProperty("operationId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(winnerOperationId));
            await Task.WhenAll(WaitForHumanInputLifecycleAsync(browser, "answered"), WaitForHumanInputLifecycleAsync(staleTab, "answered"));

            var lifecycle = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, RequestId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Answered, lifecycle.PrimarySnapshot?.Head.Status);
            var responses = await HumanInputBrowserFixture.ReadResponsesAsync(paths, capabilityTrustRoot, RequestId);
            var responseSnapshot = Assert.IsType<HumanInputResponseLifecycleStoreSnapshot>(responses.Snapshot);
            var response = Assert.Single(responseSnapshot.Responses);
            var operation = responseSnapshot.Operations.Single(item => string.Equals(item.OperationId, winnerOperationId, StringComparison.Ordinal));
            Assert.Equal(winnerOperationId, operation.OperationId);
            Assert.Equal(response.ResponseId, operation.SubmittedResponse?.ResponseId);
            app.AssertHealthy();
            await browser.AssertHealthyAsync(($"/api/human-input/{RequestId}/answer", 409));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_input_browser_uses_two_same_profile_tabs_for_concurrent_valid_response_winner_conflict_and_accessibility), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_input_browser_rereads_after_reload_reconnect_and_external_process_restart_without_persisting_operations()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanInputBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        const string RequestId = "browser-human-input-reconnect";
        await HumanInputBrowserFixture.SeedPendingAsync(paths, RequestId, "Reload and reconnect this exact request.", capabilityTrustRoot);
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        HeadlessBrowserSession? browser = null;
        string? retiredServerOutput = null;

        try
        {
            browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            await SetValueAsync(browser, "#humanInputResponseEditor textarea", "draft exists only in this tab");
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).some(key => key.toLowerCase().includes('human-input') || key.toLowerCase().includes('operation'))"));
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            Assert.Equal(string.Empty, await browser.EvaluateStringAsync("document.querySelector('#humanInputResponseEditor textarea')?.value ?? ''"));

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
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            await WaitForHumanInputLifecycleAsync(browser, "pending");
            Assert.Equal(string.Empty, await browser.EvaluateStringAsync("document.querySelector('#humanInputResponseEditor textarea')?.value ?? ''"));
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).some(key => key.toLowerCase().includes('human-input') || key.toLowerCase().includes('operation'))"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_input_browser_rereads_after_reload_reconnect_and_external_process_restart_without_persisting_operations), browser, app, retiredServerOutput);
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
    public async Task Human_input_browser_exercises_cancel_reject_late_after_expiry_and_supersede_lineage()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanInputBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        const string CancelId = "browser-human-input-cancel";
        const string RejectId = "browser-human-input-reject";
        const string ExpiryId = "browser-human-input-expiry";
        const string SupersedeId = "browser-human-input-supersede";
        await HumanInputBrowserFixture.SeedPendingAsync(paths, CancelId, "Cancel this exact request.", capabilityTrustRoot);
        await HumanInputBrowserFixture.SeedPendingAsync(paths, RejectId, "Reject this exact request.", capabilityTrustRoot);
        await HumanInputBrowserFixture.SeedPendingAsync(paths, ExpiryId, "Answer only inside this short window.", capabilityTrustRoot, requestExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(1));
        await HumanInputBrowserFixture.SeedPendingAsync(paths, SupersedeId, "Supersede this exact request.", capabilityTrustRoot);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, CancelId);
            await ClickAsync(browser, "[data-testid=\"human-input-cancel\"]");
            await WaitForHumanInputLifecycleAsync(browser, "cancelled");
            var cancelled = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, CancelId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Cancelled, cancelled.PrimarySnapshot?.Head.Status);
            Assert.Empty(Assert.IsType<HumanInputResponseLifecycleStoreSnapshot>((await HumanInputBrowserFixture.ReadResponsesAsync(paths, capabilityTrustRoot, CancelId)).Snapshot).Responses);
            await SelectHumanInputAsync(browser, RejectId);
            await ClickAsync(browser, "[data-testid=\"human-input-reject\"]");
            await WaitForHumanInputLifecycleAsync(browser, "rejected");
            var rejected = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, RejectId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Rejected, rejected.PrimarySnapshot?.Head.Status);
            Assert.Empty(Assert.IsType<HumanInputResponseLifecycleStoreSnapshot>((await HumanInputBrowserFixture.ReadResponsesAsync(paths, capabilityTrustRoot, RejectId)).Snapshot).Responses);

            await SelectHumanInputAsync(browser, ExpiryId);
            await SetValueAsync(browser, "#humanInputResponseEditor textarea", "late answer");
            await Task.Delay(TimeSpan.FromSeconds(2));
            await ClickAsync(browser, "[data-testid=\"human-input-response-submit\"]");
            await browser.WaitForExpressionAsync("document.getElementById('humanInputResponseStatus').textContent.toLowerCase().includes('window closed') || document.getElementById('humanInputResponseStatus').textContent.toLowerCase().includes('temporarily unavailable')");
            await WaitForHumanInputLifecycleAsync(browser, "expired");
            var expiry = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, ExpiryId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Expired, expiry.PrimarySnapshot?.Head.Status);
            Assert.Empty(Assert.IsType<HumanInputResponseLifecycleStoreSnapshot>((await HumanInputBrowserFixture.ReadResponsesAsync(paths, capabilityTrustRoot, ExpiryId)).Snapshot).Responses);

            await SelectHumanInputAsync(browser, SupersedeId);
            await SetValueAsync(browser, "#humanInputSupersedePurpose", "Prepared successor purpose");
            await SetValueAsync(browser, "#humanInputSupersedePrompt", "Prepared successor prompt");
            await ClickAsync(browser, "[data-testid=\"human-input-supersede\"]");
            await browser.WaitForExpressionAsync("document.getElementById('humanInputSupersedeStatus').textContent.toLowerCase().includes('prepared') && document.querySelector('[data-testid=\"human-input-supersede\"]')?.textContent.toLowerCase().includes('commit')");
            await ClickAsync(browser, "[data-testid=\"human-input-supersede\"]");
            await WaitForHumanInputLifecycleAsync(browser, "superseded");
            var superseded = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, SupersedeId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Superseded, superseded.PrimarySnapshot?.Head.Status);
            Assert.False(string.IsNullOrWhiteSpace(superseded.PrimarySnapshot?.Head.SupersededByRequestId));
            var successorId = superseded.PrimarySnapshot!.Head.SupersededByRequestId!;
            var successor = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, successorId);
            Assert.Equal(SupersedeId, successor.PrimarySnapshot?.Head.SupersedesRequestId);
            app.AssertHealthy();
            await browser.AssertHealthyAsync(($"/api/human-input/{ExpiryId}/answer", 409));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_input_browser_exercises_cancel_reject_late_after_expiry_and_supersede_lineage), browser, app);
            throw;
        }
    }

    [InstalledBrowserFact]
    public async Task Human_input_browser_denies_server_pinned_ineligible_actor_and_redacts_xss_routing_and_secret_material()
    {
        using var workspace = new TestWorkspace();
        using var serverAccount = new BrowserServerAccountDirectory(workspace.ServerStatePath);
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        var paths = new WorkspacePaths(workspace.RootPath);
        var capabilityTrustRoot = BrowserCapabilityTrustRoot(serverAccount.RootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(capabilityTrustRoot)!);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(capabilityTrustRoot).InitializeAsync(workspace.RootPath);
        var profile = HumanInputBrowserProfile();
        await InstallBrowserModelProfilesAsync(workspace.RootPath, capabilityTrustRoot, [BrowserProfileWebHost.CreateDescriptor(profile)]);
        await SeedHumanReviewReadinessAuthorityAsync(paths, capabilityTrustRoot);
        const string RequestId = "browser-human-input-foreign-actor";
        const string SecretCanary = "human-input-private-route-canary-7f9c2e";
        await HumanInputBrowserFixture.SeedPendingAsync(paths, RequestId, "<script>window.__xss = true</script> bounded sensitive prompt", capabilityTrustRoot, privacyClass: HumanInputPrivacyClass.Sensitive, eligibleRespondentId: SecretCanary);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RequestId);
            Assert.True(await browser.EvaluateBooleanAsync("window.__xss !== true && document.getElementById('humanInputPrompt').textContent.includes('<script>window.__xss = true</script>') && !document.getElementById('humanInputPrompt').innerHTML.includes('<script>window.__xss')"));
            var rendered = await browser.EvaluateStringAsync("document.body.textContent");
            Assert.DoesNotContain(SecretCanary, rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("grantReference", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorityProfile", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("workspaceId", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("access_token", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("roleId", rendered, StringComparison.OrdinalIgnoreCase);
            await SetValueAsync(browser, "#humanInputResponseEditor textarea", "foreign actor attempt");
            var actionPath = $"/api/human-input/{Uri.EscapeDataString(RequestId)}/answer";
            await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(actionPath));
            await ClickAsync(browser, "[data-testid=\"human-input-response-submit\"]");
            await browser.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1 && window.__humanInputPostCapture.statuses[0] === 403");
            var payload = await ReadHumanInputPayloadAsync(browser, "window.__humanInputPostCapture.payloads[0]");
            AssertBrowserResponsePayload(payload, RequestId);
            Assert.DoesNotContain(SecretCanary, payload.GetRawText(), StringComparison.Ordinal);
            Assert.False(await browser.EvaluateBooleanAsync($"location.href.includes('{SecretCanary}') || Object.values(localStorage).some(value => value.includes('{SecretCanary}')) || Object.values(sessionStorage).some(value => value.includes('{SecretCanary}'))"));
            var anonymousStatus = await browser.EvaluateInt32Async($"(async () => {{ const response = await fetch('/api/human-input/{Uri.EscapeDataString(RequestId)}', {{ credentials: 'omit', cache: 'no-store' }}); return response.status; }})()");
            Assert.Equal(401, anonymousStatus);
            var lifecycle = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, RequestId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Pending, lifecycle.PrimarySnapshot?.Head.Status);
            Assert.DoesNotContain(SecretCanary, app.FormatOutput(), StringComparison.Ordinal);
            app.AssertHealthy();
            await browser.AssertHealthyAsync(($"/api/human-input/{RequestId}/answer", 403), ($"/api/human-input/{RequestId}", 401));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_input_browser_denies_server_pinned_ineligible_actor_and_redacts_xss_routing_and_secret_material), browser, app);
            throw;
        }
    }

    private static BrowserModelProfileSpec HumanInputBrowserProfile()
        => new(HumanInputBrowserProfileId, "human-input", "Test-only Human Input browser model profile.", "gpt-test", true);

    private static async Task OpenHumanInputAsync(HeadlessBrowserSession browser)
    {
        await ClickAsync(browser, "[data-testid=\"human-input-nav\"]");
        await browser.WaitForExpressionAsync("!document.getElementById('humanInputView').hidden && document.getElementById('humanInputListStatus').textContent.length > 0");
    }

    private static async Task OpenHumanInputAsync(HeadlessBrowserTab tab)
    {
        await tab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-input-nav\"]')?.click()");
        await tab.WaitForExpressionAsync("!document.getElementById('humanInputView').hidden && document.getElementById('humanInputListStatus').textContent.length > 0");
    }

    private static async Task SelectHumanInputAsync(HeadlessBrowserSession browser, string requestId)
    {
        var selector = JsonSerializer.Serialize($"[data-testid=\"human-input-item\"][data-request-id=\"{requestId}\"]");
        await browser.WaitForExpressionAsync($"document.querySelector({selector}) !== null");
        await browser.EvaluateWithUserGestureAsync($"document.querySelector({selector})?.click()");
        await browser.WaitForExpressionAsync("document.getElementById('humanInputDetailPanel').hidden === false && document.getElementById('humanInputDetailStatus').textContent.includes('Canonical state reread')");
    }

    private static async Task SelectHumanInputAsync(HeadlessBrowserTab tab, string requestId)
    {
        var selector = JsonSerializer.Serialize($"[data-testid=\"human-input-item\"][data-request-id=\"{requestId}\"]");
        await tab.WaitForExpressionAsync($"document.querySelector({selector}) !== null");
        await tab.EvaluateWithUserGestureAsync($"document.querySelector({selector})?.click()");
        await tab.WaitForExpressionAsync("document.getElementById('humanInputDetailPanel').hidden === false && document.getElementById('humanInputDetailStatus').textContent.includes('Canonical state reread')");
    }

    private static async Task InitializeWorkspaceInTabAsync(HeadlessBrowserTab tab)
    {
        await tab.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized') && document.getElementById('configContent').textContent.includes('compatible-test')");
    }

    private static async Task WaitForHumanInputLifecycleAsync(HeadlessBrowserSession browser, string lifecycle)
    {
        var token = JsonSerializer.Serialize(lifecycle.Replace('-', ' '));
        await browser.WaitForExpressionAsync($"document.getElementById('humanInputLifecycleStatus').textContent.toLowerCase().includes({token})");
    }

    private static async Task WaitForHumanInputLifecycleAsync(HeadlessBrowserTab tab, string lifecycle)
    {
        var token = JsonSerializer.Serialize(lifecycle.Replace('-', ' '));
        await tab.WaitForExpressionAsync($"document.getElementById('humanInputLifecycleStatus').textContent.toLowerCase().includes({token})");
    }

    private static async Task<JsonElement> ReadHumanInputPayloadAsync(HeadlessBrowserSession browser, string expression)
    {
        var serialized = await browser.EvaluateStringAsync($"JSON.stringify({expression})");
        using var envelope = JsonDocument.Parse(serialized);
        if (envelope.RootElement.ValueKind != JsonValueKind.String)
        {
            return envelope.RootElement.Clone();
        }

        var body = envelope.RootElement.GetString();
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new JsonException("The captured Human Input response body was empty.");
        }

        using var bodyDocument = JsonDocument.Parse(body);
        return bodyDocument.RootElement.Clone();
    }

    private static async Task SetValueForTabAsync(HeadlessBrowserTab tab, string selector, string value)
    {
        var jsonSelector = JsonSerializer.Serialize(selector);
        var jsonValue = JsonSerializer.Serialize(value);
        await tab.EvaluateWithUserGestureAsync($"(() => {{ const element = document.querySelector({jsonSelector}); if (!element) throw new Error('Element was not rendered: ' + {jsonSelector}); element.value = {jsonValue}; element.dispatchEvent(new Event('input', {{ bubbles: true }})); }})()");
    }

    private static void AssertBrowserResponsePayload(JsonElement payload, string requestId)
    {
        Assert.Equal(requestId, payload.GetProperty("expectedRequest").GetProperty("requestId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("operationId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("responseId").GetString()));
        Assert.DoesNotContain("actor", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("role", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workspace", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("binding", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("grant", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authority", payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CustomLoopRunRecord> WaitForHumanInputContinuationAsync(WorkspacePaths paths, string runId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            using var store = new CustomLoopRunStore(paths);
            var run = await store.GetAsync(runId, timeout.Token);
            if (run?.Status == CustomLoopRunStatus.Completed && run.Frontier?.Payload.Status == GovernedLoopFrontierStatus.Completed)
            {
                return run;
            }

            await Task.Delay(100, timeout.Token);
        }

        throw new TimeoutException($"The Human Input continuation did not complete for run {runId}.");
    }
}
