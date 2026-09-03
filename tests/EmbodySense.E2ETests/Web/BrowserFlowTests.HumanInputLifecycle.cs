using System.Text.Json;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    [InstalledBrowserFact]
    public async Task Human_input_browser_applies_remind_reroute_and_amend_through_visible_server_owned_controls()
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

        const string RemindId = "browser-human-input-remind-conflict";
        const string RerouteId = "browser-human-input-reroute-visible";
        const string AmendId = "browser-human-input-amend-visible";
        const string PrimaryRouteCanary = "reroute-private-primary-canary";
        const string SecondaryRouteCanary = "reroute-private-secondary-canary";
        await HumanInputBrowserFixture.SeedPendingAsync(paths, RemindId, "Remind this exact request.", capabilityTrustRoot);
        await HumanInputBrowserFixture.SeedPendingWithEligibleRespondentsAsync(paths, RerouteId, "Reroute this exact request.", capabilityTrustRoot, [PrimaryRouteCanary, SecondaryRouteCanary]);
        await HumanInputBrowserFixture.SeedPendingAsync(paths, AmendId, "Amend this exact request.", capabilityTrustRoot);

        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        string? retiredServerOutput = null;

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanInputAsync(browser);
            await SelectHumanInputAsync(browser, RemindId);

            await using (var competingTab = await browser.OpenTabAsync(app.BaseUrl))
            {
                await InitializeWorkspaceInTabAsync(competingTab);
                await OpenHumanInputAsync(competingTab);
                await SelectHumanInputAsync(competingTab, RemindId);
                var remindPath = $"/api/human-input/{Uri.EscapeDataString(RemindId)}/remind";
                var fixedOperationSeed = Guid.NewGuid().ToString("N");
                await browser.EvaluateWithUserGestureAsync(HumanInputLifecycleBrowserScripts.InstallFixedOperationIdentity(fixedOperationSeed));
                await competingTab.EvaluateWithUserGestureAsync(HumanInputLifecycleBrowserScripts.InstallFixedOperationIdentity(fixedOperationSeed));
                await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCommitResponseLoss(remindPath));
                await competingTab.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(remindPath));
                await ClickAsync(browser, "[data-testid=\"human-input-remind\"]");
                await browser.WaitForExpressionAsync("window.__humanInputResponseLoss?.mode === 'post-commit-lost' && window.__humanInputResponseLoss.attempts === 1 && window.__humanInputResponseLoss.networkPosts === 1 && window.__humanInputResponseLoss.statuses[0] === 200");
                await browser.WaitForExpressionAsync("document.getElementById('humanInputResponseStatus').textContent.toLowerCase().includes('temporarily unavailable')");
                await WaitForHumanInputLifecycleAsync(browser, "pending");
                await competingTab.EvaluateWithUserGestureAsync("document.querySelector('[data-testid=\"human-input-remind\"]')?.click()");
                await competingTab.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1 && window.__humanInputPostCapture.statuses[0] === 200");
                await competingTab.WaitForExpressionAsync("document.getElementById('humanInputResponseStatus').textContent.toLowerCase().includes('already recorded')");
                var remindSnapshot = await ReadLifecycleSnapshotAsync(paths, capabilityTrustRoot, RemindId);
                Assert.Equal(HumanInputRequestLifecycleStatus.Pending, remindSnapshot.Head.Status);
                Assert.Equal(2, remindSnapshot.Head.LifecycleVersion);
                Assert.Equal(1, remindSnapshot.Head.ReminderCount);
                var remindOperations = remindSnapshot.Operations.Where(operation => operation.Kind == HumanInputRequestLifecycleOperationKind.Remind).ToArray();
                Assert.Single(remindOperations);
                Assert.Equal(HumanInputRequestLifecycleOperationOutcome.Committed, remindOperations[0].Outcome);
                var lostRemindPayload = await ReadHumanInputPayloadAsync(browser, "window.__humanInputResponseLoss.payloads[0]");
                var replayRemindPayload = await ReadHumanInputPayloadAsync(competingTab, "window.__humanInputPostCapture.payloads[0]");
                Assert.Equal(lostRemindPayload.GetProperty("operationId").GetString(), replayRemindPayload.GetProperty("operationId").GetString());
                Assert.Equal(RemindId, lostRemindPayload.GetProperty("expectedRequest").GetProperty("requestId").GetString());
                Assert.Equal(RemindId, replayRemindPayload.GetProperty("expectedRequest").GetProperty("requestId").GetString());
                AssertNoForbiddenResponsePropertyNames(lostRemindPayload);
                AssertNoForbiddenResponsePropertyNames(replayRemindPayload);
                var remindPayload = lostRemindPayload;
                Assert.Equal(RemindId, remindPayload.GetProperty("expectedRequest").GetProperty("requestId").GetString());
                Assert.False(remindPayload.TryGetProperty("candidateKey", out _));
                await browser.EvaluateWithUserGestureAsync(HumanInputLifecycleBrowserScripts.RestoreOperationIdentity());
                await competingTab.EvaluateWithUserGestureAsync(HumanInputLifecycleBrowserScripts.RestoreOperationIdentity());
            }

            await SelectHumanInputAsync(browser, RerouteId);
            await SetValueAsync(browser, "#humanInputRerouteExpiresAt", DateTimeOffset.UtcNow.AddMinutes(10).ToLocalTime().ToString("yyyy-MM-ddTHH:mm"));
            var reroutePreparePath = $"/api/human-input/{Uri.EscapeDataString(RerouteId)}/reroute/prepare";
            await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(reroutePreparePath));
            await ClickAsync(browser, "[data-testid=\"human-input-reroute\"]");
            await browser.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1 && window.__humanInputPostCapture.statuses[0] === 200 && document.getElementById('humanInputRerouteStatus').textContent.toLowerCase().includes('prepared') && document.querySelectorAll('#humanInputRerouteOptions option').length >= 1");
            var reroutePreparation = await ReadHumanInputPayloadAsync(browser, "window.__humanInputPostCapture.payloads[0]");
            Assert.Equal(RerouteId, reroutePreparation.GetProperty("expectedRequest").GetProperty("requestId").GetString());
            Assert.Equal(1, reroutePreparation.GetProperty("expectedLifecycleVersion").GetInt64());
            Assert.Equal("pending", reroutePreparation.GetProperty("expectedLifecycleStatus").GetString(), ignoreCase: true);
            Assert.DoesNotContain(PrimaryRouteCanary, reroutePreparation.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(SecondaryRouteCanary, reroutePreparation.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("respondentId", reroutePreparation.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("routingReference", reroutePreparation.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('humanInputRerouteOptions').textContent.includes('Alternative route') && !document.getElementById('humanInputRerouteOptions').textContent.includes('reroute-private')"));
            var rerouteCommitPath = $"/api/human-input/{Uri.EscapeDataString(RerouteId)}/reroute";
            await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(rerouteCommitPath));
            await ClickAsync(browser, "[data-testid=\"human-input-reroute\"]");
            await browser.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1 && window.__humanInputPostCapture.statuses[0] === 200 && document.getElementById('humanInputLifecycleStatus').textContent.toLowerCase().includes('pending')");
            var rerouteCommit = await ReadHumanInputPayloadAsync(browser, "window.__humanInputPostCapture.payloads[0]");
            Assert.Equal(RerouteId, rerouteCommit.GetProperty("expectedRequest").GetProperty("requestId").GetString());
            Assert.Equal("reroute", rerouteCommit.GetProperty("reason").GetString(), ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(rerouteCommit.GetProperty("candidateKey").GetString()));
            AssertNoForbiddenResponsePropertyNames(rerouteCommit);
            var rerouteSnapshot = await ReadLifecycleSnapshotAsync(paths, capabilityTrustRoot, RerouteId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Pending, rerouteSnapshot.Head.Status);
            Assert.Equal(2, rerouteSnapshot.Head.LifecycleVersion);
            Assert.Equal(2, rerouteSnapshot.RequestVersions.Count);
            Assert.Equal(2, rerouteSnapshot.RequestVersions[0].EligibleRespondents.Length);
            Assert.Single(rerouteSnapshot.RequestVersions[1].EligibleRespondents);
            Assert.NotEqual(rerouteSnapshot.RequestVersions[0].RequestHash, rerouteSnapshot.RequestVersions[1].RequestHash);
            Assert.Contains(rerouteSnapshot.Operations, operation => operation.Kind == HumanInputRequestLifecycleOperationKind.Reroute && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed);

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
            await SelectHumanInputAsync(browser, RerouteId);
            await WaitForHumanInputLifecycleAsync(browser, "pending");
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).some(key => key.toLowerCase().includes('human-input') || key.toLowerCase().includes('operation'))"));

            await SelectHumanInputAsync(browser, AmendId);
            await SetValueAsync(browser, "#humanInputAmendPurpose", "Amended purpose through the visible control.");
            await SetValueAsync(browser, "#humanInputAmendPrompt", "Amended prompt through the visible control.");
            await SetValueAsync(browser, "#humanInputAmendPrivacyClass", "sensitive", "change");
            await SetValueAsync(browser, "#humanInputAmendExpiresAt", DateTimeOffset.UtcNow.AddMinutes(20).ToLocalTime().ToString("yyyy-MM-ddTHH:mm"));
            var amendPreparePath = $"/api/human-input/{Uri.EscapeDataString(AmendId)}/amend/prepare";
            await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(amendPreparePath));
            await ClickAsync(browser, "[data-testid=\"human-input-amend\"]");
            await browser.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1 && window.__humanInputPostCapture.statuses[0] === 200 && document.getElementById('humanInputAmendStatus').textContent.toLowerCase().includes('prepared')");
            var amendPreparation = await ReadHumanInputPayloadAsync(browser, "window.__humanInputPostCapture.payloads[0]");
            Assert.Equal(AmendId, amendPreparation.GetProperty("expectedRequest").GetProperty("requestId").GetString());
            Assert.Equal("sensitive", amendPreparation.GetProperty("privacyClass").GetString(), ignoreCase: true);
            Assert.Equal("Amended purpose through the visible control.", amendPreparation.GetProperty("purpose").GetString());
            Assert.Equal("Amended prompt through the visible control.", amendPreparation.GetProperty("prompt").GetString());
            Assert.False(amendPreparation.TryGetProperty("candidateKey", out _));
            var amendCommitPath = $"/api/human-input/{Uri.EscapeDataString(AmendId)}/amend";
            await browser.EvaluateWithUserGestureAsync(HumanInputBrowserTransportScripts.InstallPostCapture(amendCommitPath));
            await ClickAsync(browser, "[data-testid=\"human-input-amend\"]");
            await browser.WaitForExpressionAsync("window.__humanInputPostCapture?.statuses.length === 1 && document.getElementById('humanInputLifecycleStatus').textContent.toLowerCase().includes('pending')");
            var amendCommit = await ReadHumanInputPayloadAsync(browser, "window.__humanInputPostCapture.payloads[0]");
            Assert.Equal(AmendId, amendCommit.GetProperty("expectedRequest").GetProperty("requestId").GetString());
            Assert.Equal("amend", amendCommit.GetProperty("reason").GetString(), ignoreCase: true);
            Assert.False(string.IsNullOrWhiteSpace(amendCommit.GetProperty("candidateKey").GetString()));
            AssertNoForbiddenResponsePropertyNames(amendCommit);
            var amendSnapshot = await ReadLifecycleSnapshotAsync(paths, capabilityTrustRoot, AmendId);
            Assert.Equal(HumanInputRequestLifecycleStatus.Pending, amendSnapshot.Head.Status);
            Assert.Equal(2, amendSnapshot.Head.LifecycleVersion);
            Assert.Equal(2, amendSnapshot.RequestVersions.Count);
            var amendedRequest = amendSnapshot.RequestVersions[^1];
            Assert.Equal("Amended purpose through the visible control.", amendedRequest.Purpose);
            Assert.Equal("Amended prompt through the visible control.", amendedRequest.Prompt);
            Assert.Equal(HumanInputPrivacyClass.Sensitive, amendedRequest.PrivacyClass);
            Assert.Equal(amendSnapshot.RequestVersions[0].Binding, amendedRequest.Binding);
            Assert.Contains(amendSnapshot.Operations, operation => operation.Kind == HumanInputRequestLifecycleOperationKind.Amend && operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed);
            await browser.BeginExpectedServerRestartAsync();
            retiredServerOutput = string.Concat(retiredServerOutput, Environment.NewLine, app.FormatOutput());
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
            await SelectHumanInputAsync(browser, AmendId);
            await WaitForHumanInputLifecycleAsync(browser, "pending");
            Assert.True(await browser.EvaluateBooleanAsync("document.getElementById('humanInputPurpose').textContent.includes('Amended purpose through the visible control.') && document.getElementById('humanInputPrompt').textContent.includes('Amended prompt through the visible control.')"));
            Assert.False(await browser.EvaluateBooleanAsync("Object.keys(localStorage).some(key => key.toLowerCase().includes('human-input') || key.toLowerCase().includes('operation'))"));
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_input_browser_applies_remind_reroute_and_amend_through_visible_server_owned_controls), browser, app, retiredServerOutput);
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

    private static async Task<HumanInputRequestLifecycleStoreSnapshot> ReadLifecycleSnapshotAsync(WorkspacePaths paths, string capabilityTrustRoot, string requestId)
    {
        var read = await HumanInputBrowserFixture.ReadAsync(paths, capabilityTrustRoot, requestId);
        return read.PrimarySnapshot ?? throw new InvalidOperationException($"The canonical lifecycle snapshot was not available for {requestId}: {read.Status}.");
    }

    private static async Task<JsonElement> ReadHumanInputPayloadAsync(HeadlessBrowserTab tab, string expression)
    {
        var serialized = await tab.EvaluateStringAsync($"JSON.stringify({expression})");
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
}
