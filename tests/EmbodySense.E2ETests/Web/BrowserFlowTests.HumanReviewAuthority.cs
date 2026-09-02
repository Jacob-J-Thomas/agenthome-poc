using System.Text.Json;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Persistence.Authority;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.E2EBrowserHost;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    [InstalledBrowserFact]
    public async Task Human_review_browser_rejects_approval_after_expiry_through_server_owned_runtime_without_dispatch()
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
        var runId = "browser-human-review-expiry-boundary";
        var exactExpiryUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "exact expiry boundary", capabilityTrustRoot: capabilityTrustRoot, requestExpiresAtUtc: exactExpiryUtc);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-approve\"]')?.getAttribute('aria-disabled') === 'false'"));
            await WaitUntilHumanReviewHasExpiredAsync(exactExpiryUtc);
            await ClickAsync(browser, "[data-testid=\"human-review-approve\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "expired");
            Assert.Contains("terminal", (await browser.EvaluateStringAsync("document.getElementById('humanReviewActionStatus').textContent")).ToLowerInvariant(), StringComparison.Ordinal);
            Assert.Equal(0, await browser.EvaluateInt32Async("document.querySelectorAll('#humanReviewDecisionHistory .human-review-decision-item').length"));
            await AssertNoReviewDispatchAsync(browser, runId);
            var review = await ReadHumanReviewAsync(browser, runId);
            Assert.Contains("expired", review, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("access_token", review, StringComparison.OrdinalIgnoreCase);
            app.AssertHealthy();
            await browser.AssertHealthyAsync(("/api/human-reviews/" + Uri.EscapeDataString(runId) + "/approve", 409));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_rejects_approval_after_expiry_through_server_owned_runtime_without_dispatch), browser, app);
            throw;
        }
    }

    private static async Task WaitUntilHumanReviewHasExpiredAsync(DateTimeOffset expiryUtc)
    {
        var remaining = expiryUtc - TimeProvider.System.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining).ConfigureAwait(false);
        }

        await Task.Delay(100).ConfigureAwait(false);
    }

    [InstalledBrowserFact]
    public async Task Human_review_browser_retains_consent_after_server_owned_authority_revocation_without_release()
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
        var runId = "browser-human-review-authority-revocation";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "authority revocation after consent", includePreDispatchEffect: true, capabilityTrustRoot: capabilityTrustRoot);
        var effectBefore = await ReadCanonicalEffectAttemptAsync(paths, runId);
        AssertExactNotStartedEffect(effectBefore);
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        HeadlessBrowserSession? browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);
        string? retiredServerOutput = null;

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await using var authorityParking = await HumanReviewAuthorityParkingLease.ParkAsync(paths);
            await ClickAsync(browser, "[data-testid=\"human-review-approve\"]");
            await WaitForHumanReviewLifecycleAsync(browser, "approved");
            var approved = await ReadHumanReviewAsync(browser, runId);
            AssertCanonicalApproval(approved);

            var approvedDecision = ReadApprovalDecision(approved);
            await browser.BeginExpectedServerRestartAsync();
            retiredServerOutput = app.FormatOutput();
            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            await authorityParking.RestoreAsync();
            await RetireBrowserAuthorityAsync(paths, capabilityTrustRoot);
            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, port, codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent === 'Web primary'");
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await browser.EndExpectedServerRestartAsync();
            await OpenHumanReviewAsync(browser);
            await SelectHumanReviewAsync(browser, runId);
            await WaitForCanonicalHumanReviewAsync(browser, "approved", 1);
            var reread = await ReadHumanReviewAsync(browser, runId);
            AssertCanonicalApproval(reread);
            Assert.Equal(approvedDecision, ReadApprovalDecision(reread));
            await AssertNoReviewDispatchAsync(browser, runId);
            Assert.Equal("Retired", await ReadRetiredBrowserAuthorityAsync(paths, capabilityTrustRoot));
            Assert.DoesNotContain("grantReference", reread, StringComparison.OrdinalIgnoreCase);
            var effectAfter = await ReadCanonicalEffectAttemptAsync(paths, runId);
            Assert.Equal(effectBefore.ContentHash, effectAfter.ContentHash);
            AssertExactNotStartedEffect(effectAfter);
            var evidence = await ReadHumanReviewEndpointAsync(browser, $"/api/human-reviews/{Uri.EscapeDataString(runId)}/evidence");
            Assert.StartsWith("200|", evidence, StringComparison.Ordinal);
            using var evidenceDocument = JsonDocument.Parse(evidence[4..]);
            var effectEvidence = evidenceDocument.RootElement.GetProperty("effectEvidence");
            Assert.Equal("exact-not-started", effectEvidence.GetProperty("status").GetString());
            Assert.Equal("not-started", effectEvidence.GetProperty("certainty").GetString());
            app.AssertHealthy();
            await browser.AssertHealthyAsync();
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_retains_consent_after_server_owned_authority_revocation_without_release), browser, app);
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
    public async Task Human_review_browser_blocks_ambiguous_effect_approval_and_never_redispatches()
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
        var runId = "browser-human-review-ambiguous-effect";
        await HumanReviewBrowserFixture.SeedPendingAsync(paths, runId, "ambiguous effect boundary", includePreDispatchEffect: true, makeEffectAmbiguous: true, capabilityTrustRoot: capabilityTrustRoot);
        var canonicalBefore = await ReadCanonicalRunAsync(paths, runId);
        var effectBefore = await ReadCanonicalEffectAttemptAsync(paths, runId);
        var effectArtifactCountBefore = CountEffectAttemptArtifacts(paths);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, effectBefore.Payload.Phase);
        Assert.Equal(GovernedLoopEffectEvidenceStatus.Incomplete, effectBefore.Payload.EvidenceStatus);
        await using var app = await ExternalWebApplicationProcess.StartBrowserProfileHostAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", capabilityTrustRoot, [profile]);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            var selector = JsonSerializer.Serialize($"[data-testid=\"human-review-item\"][data-run-id=\"{runId}\"]");
            await browser.EvaluateWithUserGestureAsync($"(() => {{ const item = document.querySelector({selector}); if (!item) throw new Error('Ambiguous Human Review item was not rendered.'); item.click(); }})()");
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewDetailPanel').hidden === false && document.querySelector('[data-testid=\"human-review-approve\"]')?.getAttribute('aria-disabled') === 'true'");
            Assert.True(await browser.EvaluateBooleanAsync("document.querySelector('[data-testid=\"human-review-approve\"]')?.getAttribute('aria-disabled') === 'true'"));
            var expectedLifecycleVersion = await browser.EvaluateInt32Async($"(async () => {{ const response = await fetch('/api/human-reviews/{Uri.EscapeDataString(runId)}', {{ cache: 'no-store' }}); const body = await response.json(); return body.detail.summary.lifecycleVersion; }})()");
            var tamperedDecisionStatus = await browser.EvaluateInt32Async($"(async () => {{ const response = await fetch('/api/human-reviews/{Uri.EscapeDataString(runId)}/approve', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: JSON.stringify({{ expectedLifecycleVersion: {expectedLifecycleVersion}, operationId: 'ambiguous-effect-negative', effectAttemptId: 'forged-effect', effectEvidence: 'conclusive' }}) }}); return response.status; }})()");
            Assert.Equal(400, tamperedDecisionStatus);
            var detailStatus = (await browser.EvaluateStringAsync("document.getElementById('humanReviewDetailStatus').textContent")).ToLowerInvariant();
            Assert.True(detailStatus.Contains("conflicting", StringComparison.Ordinal) || detailStatus.Contains("unavailable", StringComparison.Ordinal));
            var evidence = await ReadHumanReviewEndpointAsync(browser, $"/api/human-reviews/{Uri.EscapeDataString(runId)}/evidence");
            Assert.StartsWith("409|", evidence, StringComparison.Ordinal);
            Assert.Contains("ambiguous", evidence, StringComparison.OrdinalIgnoreCase);
            var rereadEvidence = await ReadHumanReviewEndpointAsync(browser, $"/api/human-reviews/{Uri.EscapeDataString(runId)}/evidence");
            Assert.Equal(evidence, rereadEvidence);
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenHumanReviewAsync(browser);
            await browser.EvaluateWithUserGestureAsync($"(() => {{ const item = document.querySelector({selector}); if (!item) throw new Error('Ambiguous Human Review item disappeared.'); item.click(); }})()");
            await browser.WaitForExpressionAsync("document.getElementById('humanReviewDetailPanel').hidden === false && document.querySelector('[data-testid=\"human-review-approve\"]')?.getAttribute('aria-disabled') === 'true'");
            var afterReloadEvidence = await ReadHumanReviewEndpointAsync(browser, $"/api/human-reviews/{Uri.EscapeDataString(runId)}/evidence");
            Assert.Equal(evidence, afterReloadEvidence);
            var effectAfter = await ReadCanonicalEffectAttemptAsync(paths, runId);
            Assert.Equal(effectBefore.ContentHash, effectAfter.ContentHash);
            Assert.Equal(effectBefore.Payload.Phase, effectAfter.Payload.Phase);
            Assert.Equal(effectBefore.Payload.EvidenceStatus, effectAfter.Payload.EvidenceStatus);
            Assert.Equal(effectArtifactCountBefore, CountEffectAttemptArtifacts(paths));
            Assert.Equal(canonicalBefore, await ReadCanonicalRunAsync(paths, runId));
            app.AssertHealthy();
            await AssertAmbiguousBrowserHealthyAsync(browser, runId);
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Human_review_browser_blocks_ambiguous_effect_approval_and_never_redispatches), browser, app);
            throw;
        }
    }

    private static async Task RetireBrowserAuthorityAsync(WorkspacePaths paths, string capabilityTrustRoot)
    {
        var store = new AuthorityProfileStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var read = await store.ReadAsync("human-review-browser");
        Assert.True(read.Status is AuthorityProfileReadStatus.Available or AuthorityProfileReadStatus.RecoveredLastProved, read.Detail);
        var record = Assert.IsType<AuthorityProfileRecord>(read.Record);
        Assert.True(AuthorityActorId.TryParse("browser-e2e", out var actor, out var actorError), actorError?.ToString());
        Assert.True(AuthorityPurpose.TryParse("human-review-browser-e2e", out var reason, out var reasonError), reasonError?.ToString());
        var mutation = await store.MutateAsync(new AuthorityProfileMutation(
            AuthorityProfileMutationKind.TransitionStatus,
            "retire-human-review-browser-authority-" + Guid.NewGuid().ToString("N"),
            record.CurrentProfile.Revision.Value,
            null,
            record.ProfileId,
            AuthorityProfileStatus.Retired,
            actor!,
            reason!));
        Assert.Equal(AuthorityProfileMutationStatus.Applied, mutation.Status);
    }

    private static async Task<string> ReadRetiredBrowserAuthorityAsync(WorkspacePaths paths, string capabilityTrustRoot)
    {
        var store = new AuthorityProfileStore(paths, new FileCapabilityCatalogTrustProvider(capabilityTrustRoot));
        var read = await store.ReadAsync("human-review-browser");
        Assert.True(read.Status is AuthorityProfileReadStatus.Available or AuthorityProfileReadStatus.RecoveredLastProved, read.Detail);
        var record = Assert.IsType<AuthorityProfileRecord>(read.Record);
        return record.CurrentProfile.Status.ToString();
    }

    private static Task<string> ReadHumanReviewEndpointAsync(HeadlessBrowserSession browser, string route)
    {
        var encodedRoute = JsonSerializer.Serialize(route);
        return browser.EvaluateStringAsync($"(async () => {{ const response = await fetch({encodedRoute}, {{ cache: 'no-store' }}); return response.status + '|' + await response.text(); }})()");
    }

    private static async Task<GovernedLoopEffectAttempt> ReadCanonicalEffectAttemptAsync(WorkspacePaths paths, string runId)
    {
        using var runs = new CustomLoopRunStore(paths);
        var run = await runs.GetAsync(runId);
        var effectBinding = run?.HumanReview?.Request.Binding.EffectAttempt
            ?? throw new InvalidOperationException($"Canonical Human Review effect binding for {runId} was not found.");
        var read = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(run!.HumanReview!.Request.Binding.WorkspaceId, effectBinding.OperationId, effectBinding.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, read.Status);
        return Assert.IsType<GovernedLoopEffectAttempt>(read.Attempt);
    }

    private static void AssertExactNotStartedEffect(GovernedLoopEffectAttempt attempt)
    {
        Assert.Equal(GovernedLoopEffectPhase.IntentPrepared, attempt.Payload.Phase);
        Assert.Equal(GovernedLoopEffectOutcome.None, attempt.Payload.Outcome);
        Assert.Equal(GovernedLoopEffectEvidenceStatus.Pending, attempt.Payload.EvidenceStatus);
        Assert.Null(attempt.DispatchAuthorityEvidenceHash);
        Assert.Null(attempt.AfterEvidenceId);
    }

    private static int CountEffectAttemptArtifacts(WorkspacePaths paths)
        => Directory.Exists(paths.GovernedLoopEffectAttemptsPath)
            ? Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*", SearchOption.AllDirectories).Count()
            : 0;

    private static string ReadApprovalDecision(string serializedReview)
    {
        using var document = JsonDocument.Parse(serializedReview);
        var decision = document.RootElement.GetProperty("detail").GetProperty("decisions").EnumerateArray().Single();
        return decision.GetProperty("operationId").GetString() + "|" + decision.GetProperty("decisionHash").GetString();
    }

    private static async Task AssertAmbiguousBrowserHealthyAsync(HeadlessBrowserSession browser, string runId)
    {
        var runFragment = "/api/human-reviews/" + Uri.EscapeDataString(runId);
        var evidenceFragment = runFragment + "/evidence";
        var decisionFragment = runFragment + "/approve";
        var diagnostics = browser.DiagnosticsSnapshot();
        var evidence409 = diagnostics.Any(item => item.Contains(evidenceFragment, StringComparison.Ordinal) && (item.Contains("\"status\":409", StringComparison.Ordinal) || item.Contains("status of 409", StringComparison.Ordinal)));
        var evidence503 = diagnostics.Any(item => item.Contains(evidenceFragment, StringComparison.Ordinal) && item.Contains("\"status\":503", StringComparison.Ordinal));
        var detail409 = diagnostics.Any(item => item.Contains(runFragment, StringComparison.Ordinal) && !item.Contains(evidenceFragment, StringComparison.Ordinal) && (item.Contains("\"status\":409", StringComparison.Ordinal) || item.Contains("status of 409", StringComparison.Ordinal)));
        var detail503 = diagnostics.Any(item => item.Contains(runFragment, StringComparison.Ordinal) && !item.Contains(evidenceFragment, StringComparison.Ordinal) && (item.Contains("\"status\":503", StringComparison.Ordinal) || item.Contains("status of 503", StringComparison.Ordinal)));
        var decision400 = diagnostics.Any(item => item.Contains(decisionFragment, StringComparison.Ordinal) && (item.Contains("\"status\":400", StringComparison.Ordinal) || item.Contains("status of 400", StringComparison.Ordinal)));
        var expectedFailures = new List<(string UrlFragment, int StatusCode)>();
        if (evidence409)
        {
            expectedFailures.Add((evidenceFragment, 409));
        }

        if (evidence503)
        {
            expectedFailures.Add((evidenceFragment, 503));
        }

        if (detail409)
        {
            expectedFailures.Add((runFragment, 409));
        }

        if (detail503)
        {
            expectedFailures.Add((runFragment, 503));
        }

        if (decision400)
        {
            expectedFailures.Add((decisionFragment, 400));
        }

        await browser.AssertHealthyAsync(expectedFailures.ToArray());
    }
}
