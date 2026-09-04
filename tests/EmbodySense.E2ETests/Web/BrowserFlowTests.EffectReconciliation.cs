using System.Net;
using System.Text.Json;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Sequential.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring;
using EmbodySense.Core.Persistence.Loops.Execution.Reconciliation;
using EmbodySense.Core.Persistence.Loops.Revisions;
using EmbodySense.Core.Persistence.Permissions;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.E2ETests.Web;

public sealed partial class BrowserFlowTests
{
    [Fact]
    public async Task Effect_reconciliation_browser_fixture_retains_one_exact_non_dispatchable_ambiguity()
    {
        using var workspace = new TestWorkspace();
        var trustRoot = Path.Combine(workspace.ServerStatePath, "capability-catalog");
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedHumanReviewReadinessAuthorityAsync(paths, trustRoot);

        var seeded = await HumanReviewBrowserFixture.SeedEffectReconciliationAsync(paths, "browser-effect-reconciliation-fixture", "fixture ambiguity", trustRoot);

        using var runs = new CustomLoopRunStore(paths);
        var run = await runs.GetAsync(seeded.RunId);
        var page = await runs.ListPageAsync(new CustomLoopRunPageRequest(50));
        var effect = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(seeded.Binding.WorkspaceId, seeded.Binding.OperationId, seeded.Binding.EffectGeneration);
        Assert.NotNull(run);
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, run.Status);
        Assert.Contains(page.Items, item => string.Equals(item.Id, seeded.RunId, StringComparison.Ordinal) && item.Status == CustomLoopRunStatus.NeedsReview);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, run.Frontier?.Payload.Status);
        Assert.Single(run.Events, item => item.EffectReconciliationBinding is not null);
        Assert.Equal(seeded.Binding, run.Events.Single(item => item.EffectReconciliationBinding is not null).EffectReconciliationBinding);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, effect.Status);
        Assert.Equal(seeded.Attempt.ContentHash, effect.Attempt?.ContentHash);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, effect.Attempt?.Payload.Phase);
        Assert.Equal(GovernedLoopEffectEvidenceStatus.Incomplete, effect.Attempt?.Payload.EvidenceStatus);
        Assert.Equal("reviewed marker", seeded.MarkerContent);
        Assert.Equal(seeded.MarkerContent, await File.ReadAllTextAsync(seeded.MarkerPath));

        var cases = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(paths));
        var casePage = await cases.ListAsync(new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseListRequest(1));
        var caseSummary = Assert.Single(casePage.Cases);
        var caseReference = new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseReference(caseSummary.CaseId, caseSummary.CaseVersion, caseSummary.ContentHash, caseSummary.BindingHash);
        var caseRead = await cases.ReadAsync(new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseReadRequest(caseReference));
        var reconciliationCase = Assert.IsType<EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCase>(caseRead.Case);
        var source = Assert.Single(reconciliationCase.EvidenceSources);
        var context = new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationProbeReservationContext(
            caseReference,
            reconciliationCase.Binding,
            reconciliationCase.ContractMetadata,
            effect.Attempt!,
            source,
            new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationProbeTarget(effect.Attempt!.TargetFingerprint, effect.Attempt.PreconditionEvidenceHash, effect.Attempt.BeforeEvidenceId),
            effect.Attempt.InputFingerprint);
        var reservation = await cases.ReserveAsync(new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationProbeReservationRequest(
            "browser-effect-reconciliation-fixture-probe",
            new string('c', 64),
            context));
        Assert.Equal(EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationProbeReservationStatus.Reserved, reservation.Status);
    }

    [InstalledBrowserFact]
    public async Task Effect_reconciliation_browser_converges_response_loss_replay_conflict_reload_and_restart_without_redispatch()
    {
        using var workspace = new TestWorkspace();
        var trustRoot = Path.Combine(workspace.ServerStatePath, "capability-catalog");
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedHumanReviewReadinessAuthorityAsync(paths, trustRoot);
        var seeded = await HumanReviewBrowserFixture.SeedEffectReconciliationAsync(paths, "browser-effect-reconciliation-applied", "prove the applied outcome", trustRoot);
        var effectArtifactsBefore = EffectAttemptArtifactInventory(paths);
        var markerTimestampBefore = File.GetLastWriteTimeUtc(seeded.MarkerPath);
        var environment = new Dictionary<string, string>
        {
            [FileCapabilityCatalogTrustProvider.DefaultRootEnvironmentVariable] = trustRoot,
        };
        var port = GetFreePort();
        ExternalWebApplicationProcess? app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test", environment);
        HeadlessBrowserSession? browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenEffectReconciliationAsync(browser, seeded.CaseId);
            await AssertEffectReconciliationInputRemainsExactAsync(paths, trustRoot, seeded);
            await AssertEffectReconciliationValueFreeAsync(browser, workspace, seeded);
            await using var replayTab = await browser.OpenTabAsync(app.BaseUrl);
            await using var conflictTab = await browser.OpenTabAsync(app.BaseUrl);
            await OpenEffectReconciliationAsync(replayTab, seeded.CaseId);
            await OpenEffectReconciliationAsync(conflictTab, seeded.CaseId);
            var probePath = $"/api/effect-reconciliation/{Uri.EscapeDataString(seeded.CaseId)}/probe";
            await browser.EvaluateWithUserGestureAsync(EffectReconciliationBrowserTransportScripts.InstallPostCommitResponseLoss(probePath));
            await ClickAsync(browser, "#effectReconciliationProbeButton");
            await browser.WaitForExpressionAsync("window.__effectReconciliationTransport?.mode === 'response-lost' && window.__effectReconciliationTransport.realCommitStatus === 200");
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Applied succeeded')");

            await replayTab.EvaluateWithUserGestureAsync("document.getElementById('effectReconciliationProbeButton').click()");
            await replayTab.WaitForExpressionAsync("document.getElementById('effectReconciliationActionStatus').textContent.toLowerCase().includes('already recorded')");
            await replayTab.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Applied succeeded')");
            await conflictTab.EvaluateWithUserGestureAsync("document.getElementById('effectReconciliationAssessButton').click()");
            await conflictTab.WaitForExpressionAsync("document.getElementById('effectReconciliationActionStatus').textContent.toLowerCase().includes('conflicted')");
            await conflictTab.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Applied succeeded')");

            await ClickAsync(browser, "#effectReconciliationAssessButton");
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Proved applied succeeded')");
            await SetValueAsync(browser, "#effectReconciliationDispositionKind", "accept-proved-applied", "change");
            await SetValueAsync(browser, "#effectReconciliationDispositionDetail", "operator accepted the value-free proof");
            await ClickAsync(browser, "#effectReconciliationDisposeButton");
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationPosture').textContent.toLowerCase().includes('accepted')");

            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenEffectReconciliationAsync(browser, seeded.CaseId);
            Assert.Contains("Accepted", await browser.EvaluateStringAsync("document.getElementById('effectReconciliationPosture').textContent"), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("No immutable resolution", await browser.EvaluateStringAsync("document.getElementById('effectReconciliationResolution').textContent"), StringComparison.OrdinalIgnoreCase);

            await browser.BeginExpectedServerRestartAsync();
            await app.DisposeAsync();
            app = null;
            await browser.WaitForExpressionAsync("/reconnect|retry/i.test(document.getElementById('clientStatus').textContent)");
            browser.MarkExpectedReplacementServerStarting();
            app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, port, codexExecutable, "gpt-test", environment);
            await browser.WaitForExpressionAsync("document.getElementById('clientStatus').textContent === 'Web primary'");
            await browser.ReloadAsync();
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenEffectReconciliationAsync(browser, seeded.CaseId);
            await browser.EndExpectedServerRestartAsync();
            Assert.Contains("Accepted", await browser.EvaluateStringAsync("document.getElementById('effectReconciliationPosture').textContent"), StringComparison.OrdinalIgnoreCase);
            await AssertEffectReconciliationAttemptUnchangedAsync(paths, seeded, effectArtifactsBefore, markerTimestampBefore);
            app.AssertHealthy();
            var collectionPath = "/api/effect-reconciliation?maximumCount=50";
            var escapedCaseId = Uri.EscapeDataString(seeded.CaseId);
            var casePath = $"/api/effect-reconciliation/{escapedCaseId}?";
            var resolutionPath = $"/api/effect-reconciliation/{escapedCaseId}/resolution";
            await browser.AssertHealthyAsync([(resolutionPath, 404)], [(collectionPath, 503), (casePath, 503), (resolutionPath, 503)]);
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Effect_reconciliation_browser_converges_response_loss_replay_conflict_reload_and_restart_without_redispatch), browser, app);
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
    public async Task Effect_reconciliation_browser_applies_proved_not_applied_and_quarantine_dispositions_without_dispatch()
    {
        using var workspace = new TestWorkspace();
        var trustRoot = Path.Combine(workspace.ServerStatePath, "capability-catalog");
        var codexExecutable = await FakeCodexExecutable.CreateCompatibleAsync(workspace, "gpt-test");
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(trustRoot).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await SeedHumanReviewReadinessAuthorityAsync(paths, trustRoot);
        var notApplied = await HumanReviewBrowserFixture.SeedEffectReconciliationAsync(paths, "browser-effect-reconciliation-not-applied", "prove the absent outcome", trustRoot, retainAppliedOutcome: false);
        await HumanReviewBrowserFixture.SeedAuthoritativeNotAppliedObservationAsync(paths, notApplied);
        var quarantine = await HumanReviewBrowserFixture.SeedEffectReconciliationAsync(paths, "browser-effect-reconciliation-quarantine", "retain an inconclusive outcome", trustRoot, retainAppliedOutcome: false);
        var effectArtifactsBefore = EffectAttemptArtifactInventory(paths);
        Assert.False(File.Exists(notApplied.MarkerPath));
        Assert.False(File.Exists(quarantine.MarkerPath));
        var environment = new Dictionary<string, string>
        {
            [FileCapabilityCatalogTrustProvider.DefaultRootEnvironmentVariable] = trustRoot,
        };
        await using var app = await ExternalWebApplicationProcess.StartAsync(workspace.RootPath, GetFreePort(), codexExecutable, "gpt-test", environment);
        await using var browser = await HeadlessBrowserSession.StartAsync(app.BaseUrl);

        try
        {
            await InitializeWorkspaceAsyncIfNeededAsync(browser);
            await OpenEffectReconciliationAsync(browser, notApplied.CaseId);
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Not applied')");
            await ClickAsync(browser, "#effectReconciliationAssessButton");
            await WaitForEffectReconciliationListPostureAsync(browser, notApplied.CaseId, "assessed");
            await OpenEffectReconciliationAsync(browser, notApplied.CaseId);
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Proved not applied')");
            await SetValueAsync(browser, "#effectReconciliationDispositionKind", "accept-proved-not-applied", "change");
            await ClickAsync(browser, "#effectReconciliationDisposeButton");
            await WaitForEffectReconciliationListPostureAsync(browser, notApplied.CaseId, "accepted");
            await OpenEffectReconciliationAsync(browser, notApplied.CaseId);
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationPosture').textContent.toLowerCase().includes('accepted')");

            await OpenEffectReconciliationAsync(browser, quarantine.CaseId);
            await ClickAsync(browser, "#effectReconciliationAssessButton");
            await WaitForEffectReconciliationListPostureAsync(browser, quarantine.CaseId, "assessed");
            await OpenEffectReconciliationAsync(browser, quarantine.CaseId);
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationEvidence').textContent.includes('Inconclusive')");
            await SetValueAsync(browser, "#effectReconciliationDispositionKind", "quarantine-unresolved", "change");
            await SetValueAsync(browser, "#effectReconciliationDispositionDetail", "operator retained the unresolved ambiguity");
            await ClickAsync(browser, "#effectReconciliationDisposeButton");
            await WaitForEffectReconciliationListPostureAsync(browser, quarantine.CaseId, "quarantined");
            await OpenEffectReconciliationAsync(browser, quarantine.CaseId);
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationPosture').textContent.toLowerCase().includes('quarantined')");

            Assert.Equal(effectArtifactsBefore, EffectAttemptArtifactInventory(paths));
            Assert.False(File.Exists(notApplied.MarkerPath));
            Assert.False(File.Exists(quarantine.MarkerPath));
            app.AssertHealthy();
            await browser.AssertHealthyAsync(
                ($"/api/effect-reconciliation/{Uri.EscapeDataString(notApplied.CaseId)}/resolution", 404),
                ($"/api/effect-reconciliation/{Uri.EscapeDataString(quarantine.CaseId)}/resolution", 404));
        }
        catch
        {
            await WriteFailureDiagnosticsAsync(nameof(Effect_reconciliation_browser_applies_proved_not_applied_and_quarantine_dispositions_without_dispatch), browser, app);
            throw;
        }
    }

    private static async Task OpenEffectReconciliationAsync(HeadlessBrowserSession browser, string caseId)
    {
        await browser.WaitForExpressionAsync("!document.getElementById('effectReconciliationNav').disabled");
        await ClickAsync(browser, "#effectReconciliationNav");
        var caseIdJson = JsonSerializer.Serialize(caseId);
        await browser.WaitForExpressionAsync("!document.getElementById('effectReconciliationView').hidden && document.getElementById('effectReconciliationList').getAttribute('aria-busy') === 'false' && document.getElementById('effectReconciliationListStatus').textContent.includes('canonical state')");
        await browser.WaitForExpressionAsync($"document.getElementById('effectReconciliationList').textContent.includes({caseIdJson})");
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await browser.EvaluateWithUserGestureAsync($"(() => {{ const item = [...document.querySelectorAll('#effectReconciliationList button')].find((candidate) => candidate.textContent.includes({caseIdJson})); if (!item) throw new Error('Effect Reconciliation case was not rendered.'); item.click(); }})()");
            await browser.WaitForExpressionAsync("!document.getElementById('effectReconciliationDetailStatus').textContent.includes('Rereading')");
            if (await browser.EvaluateBooleanAsync($"!document.getElementById('effectReconciliationDetailPanel').hidden && document.getElementById('effectReconciliationIdentity').textContent.includes({caseIdJson}) && document.getElementById('effectReconciliationDetailStatus').textContent.includes('successfully')"))
            {
                return;
            }

            await ClickAsync(browser, "#effectReconciliationRefreshButton");
            await browser.WaitForExpressionAsync("document.getElementById('effectReconciliationList').getAttribute('aria-busy') === 'false'");
        }

        Assert.Fail($"Effect Reconciliation case {caseId} did not become readable after three visible refresh attempts.");
    }

    private static Task WaitForEffectReconciliationListPostureAsync(HeadlessBrowserSession browser, string caseId, string posture)
    {
        var caseIdJson = JsonSerializer.Serialize(caseId);
        var postureJson = JsonSerializer.Serialize(posture);
        return browser.WaitForExpressionAsync($"[...document.querySelectorAll('#effectReconciliationList button')].some((candidate) => candidate.textContent.includes({caseIdJson}) && candidate.textContent.toLowerCase().includes({postureJson}))");
    }

    private static async Task OpenEffectReconciliationAsync(HeadlessBrowserTab tab, string caseId)
    {
        await tab.WaitForExpressionAsync("document.getElementById('workspaceStatus').textContent.includes('Initialized') && !document.getElementById('effectReconciliationNav').disabled");
        await tab.EvaluateWithUserGestureAsync("document.getElementById('effectReconciliationNav').click()");
        var caseIdJson = JsonSerializer.Serialize(caseId);
        await tab.WaitForExpressionAsync("!document.getElementById('effectReconciliationView').hidden && document.getElementById('effectReconciliationListStatus').textContent.includes('canonical state')");
        await tab.WaitForExpressionAsync($"document.getElementById('effectReconciliationList').textContent.includes({caseIdJson})");
        await tab.EvaluateWithUserGestureAsync($"(() => {{ const item = [...document.querySelectorAll('#effectReconciliationList button')].find((candidate) => candidate.textContent.includes({caseIdJson})); if (!item) throw new Error('Effect Reconciliation case was not rendered.'); item.click(); }})()");
        await tab.WaitForExpressionAsync($"!document.getElementById('effectReconciliationDetailPanel').hidden && document.getElementById('effectReconciliationIdentity').textContent.includes({caseIdJson}) && document.getElementById('effectReconciliationDetailStatus').textContent.includes('successfully')");
    }

    private static async Task AssertEffectReconciliationValueFreeAsync(HeadlessBrowserSession browser, TestWorkspace workspace, EffectReconciliationBrowserSeed seeded)
    {
        var pageText = await browser.EvaluateStringAsync("document.getElementById('effectReconciliationView').textContent");
        Assert.DoesNotContain(workspace.RootPath, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain("process-observable-marker", pageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(seeded.MarkerContent, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Binding.OperationId, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Binding.EffectId, pageText, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", pageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorization", pageText, StringComparison.OrdinalIgnoreCase);

        using var client = new HttpClient { BaseAddress = new Uri(await browser.EvaluateStringAsync("location.origin")) };
        using var response = await client.GetAsync("/api/effect-reconciliation");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task AssertEffectReconciliationAttemptUnchangedAsync(
        WorkspacePaths paths,
        EffectReconciliationBrowserSeed seeded,
        IReadOnlyList<string> expectedArtifactInventory,
        DateTime expectedMarkerTimestampUtc)
    {
        var current = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(seeded.Binding.WorkspaceId, seeded.Binding.OperationId, seeded.Binding.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, current.Status);
        Assert.Equal(seeded.Attempt.ContentHash, current.Attempt?.ContentHash);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, current.Attempt?.Payload.Phase);
        Assert.Equal(expectedArtifactInventory, EffectAttemptArtifactInventory(paths));
        Assert.Equal(seeded.MarkerContent, await File.ReadAllTextAsync(seeded.MarkerPath));
        Assert.Equal(expectedMarkerTimestampUtc, File.GetLastWriteTimeUtc(seeded.MarkerPath));
    }

    private static string[] EffectAttemptArtifactInventory(WorkspacePaths paths)
        => Directory.EnumerateFiles(paths.GovernedLoopEffectAttemptsPath, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.StartsWith("reconciliation-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray()!;

    private static async Task AssertEffectReconciliationInputRemainsExactAsync(WorkspacePaths paths, string trustRoot, EffectReconciliationBrowserSeed seeded)
    {
        using var runs = new CustomLoopRunStore(paths);
        var run = Assert.IsType<CustomLoopRunRecord>(await runs.GetAsync(seeded.RunId));
        Assert.True(CustomLoopRunValidator.Validate(run).IsValid);
        var adapter = Assert.IsType<GovernedLoopSequentialAdapterBinding>(run.SequentialAdapterBinding);
        var frontier = Assert.IsType<GovernedLoopFrontierPosture>(run.Frontier);
        Assert.Equal(CustomLoopRunStatus.NeedsReview, run.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, frontier.Payload.Status);
        Assert.Equal(seeded.Binding.Execution, adapter.ExecutionBinding);
        Assert.Equal(seeded.Binding.Execution, frontier.Binding);
        Assert.Equal(seeded.Binding.WorkspaceId, adapter.WorkspaceId);
        Assert.Equal(seeded.Binding.WorkspaceId, frontier.WorkspaceId);
        Assert.Equal(adapter.GraphArtifactHash, frontier.GraphArtifactHash);
        Assert.Equal(adapter.GraphLayoutHash, frontier.GraphLayoutHash);
        Assert.Equal(adapter.AdmissionReceiptHash, frontier.AdmissionReceiptHash);
        var node = Assert.Single(frontier.Payload.Nodes, candidate => candidate.ActivationOrdinal == seeded.Binding.ActivationOrdinal && candidate.VisitOrdinal == seeded.Binding.VisitOrdinal);
        Assert.Equal(GovernedLoopNodeExecutionStatus.ReviewBlocked, node.Status);
        Assert.Equal(seeded.Binding.NodeId, node.NodeId);
        Assert.Equal(seeded.Binding.NodeAttempt, node.Attempt);

        var effectRead = await new GovernedLoopEffectAttemptStore(paths).ReadAsync(seeded.Binding.WorkspaceId, seeded.Binding.OperationId, seeded.Binding.EffectGeneration);
        var effect = Assert.IsType<GovernedLoopEffectAttempt>(effectRead.Attempt);
        Assert.Equal(seeded.Binding.CurrentAttemptHash, effect.ContentHash);
        Assert.Equal(GovernedLoopEffectPhase.ReconciliationRequired, effect.Payload.Phase);
        Assert.Equal(seeded.Binding.Execution, effect.Binding);
        Assert.Equal(seeded.Binding.NodeId, effect.NodeId);
        Assert.Equal(seeded.Binding.NodeAttempt, effect.NodeAttempt);
        Assert.Equal(seeded.Binding.EffectId, effect.Payload.EffectId);
        Assert.Equal(seeded.Binding.OperationId, effect.Payload.OperationId);
        Assert.Equal(seeded.Binding.EffectGeneration, effect.Payload.EffectGeneration);
        Assert.Equal(seeded.Binding.IntentHash, effect.Payload.IntentHash);
        var cases = new GovernedLoopEffectReconciliationCaseStore(new GovernedLoopEffectAttemptStore(paths));
        var page = await cases.ListAsync(new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseListRequest(1));
        var summary = Assert.Single(page.Cases);
        var reference = new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseReference(summary.CaseId, summary.CaseVersion, summary.ContentHash, summary.BindingHash);
        var caseRead = await cases.ReadAsync(new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCaseReadRequest(reference));
        Assert.True(GovernedLoopEffectReconciliationContract.Validate(Assert.IsType<EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCase>(caseRead.Case), effect).IsValid);

        var transaction = new CapabilityAuthorityTransaction(paths);
        var trust = new FileCapabilityCatalogTrustProvider(trustRoot);
        var lifecycle = new GovernedLoopRevisionLifecycleStore(paths, trust, authorityTransaction: transaction);
        var graphRead = await new GovernedLoopGraphRevisionStore(paths, lifecycle, trust, authorityTransaction: transaction).ReadArtifactAsync(seeded.Binding.Execution.Revision);
        var graph = Assert.IsType<GovernedLoopGraphRevisionArtifact>(graphRead.Artifact);
        Assert.Equal(graph.ArtifactHash, adapter.GraphArtifactHash);
        Assert.Equal(graph.LayoutHash, adapter.GraphLayoutHash);
        var graphNode = Assert.Single(graph.Graph.Nodes, candidate => string.Equals(candidate.Id, seeded.Binding.NodeId, StringComparison.Ordinal));
        Assert.Equal(graphNode.Descriptor, node.Descriptor);
        Assert.True(WorkspaceActionNodeDescriptors.TryResolve(graphNode.Descriptor, out var kind));
        Assert.True(graphNode.Parameters.TryGetValue("input", out var canonicalInput));
        Assert.True(WorkspaceActionInputContract.TryParse(canonicalInput, kind, out var workspaceInput, out var workspaceInputError), workspaceInputError);
        Assert.Equal(canonicalInput, WorkspaceActionInputContract.Encode(workspaceInput!));
        Assert.True(GovernedActuatorInputContract.TryCanonicalize(canonicalInput, out var input, out _));
        Assert.Equal(effect.InputFingerprint, input!.Fingerprint);

        var registry = GovernedWorkspaceActionFactory.CreateRegistry(paths, transaction, new ToolPermissionService(paths, new PermissionPolicyStore().Load(paths)));
        var descriptor = Assert.Single(registry.Descriptors, candidate => string.Equals(candidate.OperationId, WorkspaceActionOperationIds.For(kind), StringComparison.Ordinal));
        Assert.True(registry.TryResolve(descriptor, out var operation));
        Assert.NotNull(operation);
        var reconstructed = new EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationInputReadResult(
            EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationInputReadStatus.Found,
            reference,
            seeded.Binding,
            effect,
            frontier,
            input);
        Assert.Equal(EmbodySense.Core.Application.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationInputReadStatus.Found, reconstructed.Status);
    }
}
