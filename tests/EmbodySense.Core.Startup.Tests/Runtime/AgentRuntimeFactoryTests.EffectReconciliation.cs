using System.Text.Json;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Clients.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation;
using EmbodySense.Core.Startup.Loops.Execution.Reconciliation.Models;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;
using CommonReconciliationModels = EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeFactoryTests
{
    [Fact]
    public async Task Effect_reconciliation_facade_keeps_empty_missing_invalid_and_canceled_reads_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace);
        var missing = Reference("missing");

        var page = await runtime.EffectReconciliation.ListAsync();
        var catalog = await runtime.EffectReconciliation.ListProbeContractsAsync();

        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Ready, page.Status);
        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
        Assert.Equal(GovernedLoopEffectReconciliationProbeCatalogStatus.Ready, catalog.Status);
        Assert.Empty(catalog.Contracts);
        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Invalid, (await runtime.EffectReconciliation.ListAsync(new GovernedLoopEffectReconciliationPageRequest(1, "foreign-cursor"))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationReadStatus.NotFound, (await runtime.EffectReconciliation.ReadAsync(missing)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, (await runtime.EffectReconciliation.ReadResolutionAsync(missing)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await runtime.EffectReconciliation.ProbeAsync("probe-missing", missing)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await runtime.EffectReconciliation.AssessAsync("assess-missing", missing)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await runtime.EffectReconciliation.DisposeAsync("dispose-missing", missing, GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await runtime.EffectReconciliation.ResolveAsync("resolve-missing", missing)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationReadStatus.Invalid, (await runtime.EffectReconciliation.ReadAsync(null)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Invalid, (await runtime.EffectReconciliation.ReadResolutionAsync(null)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Invalid, (await runtime.EffectReconciliation.ListAsync(null)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, (await runtime.EffectReconciliation.ListProbeContractsAsync(null)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await runtime.EffectReconciliation.ProbeAsync("probe-missing-reference", null)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await runtime.EffectReconciliation.AssessAsync(string.Empty, missing)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await runtime.EffectReconciliation.AssessAsync("assess-oversized-detail", missing, new string('a', 1_025))).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await runtime.EffectReconciliation.DisposeAsync("dispose-invalid", missing, GovernedLoopEffectReconciliationDispositionKind.Unknown)).Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, (await runtime.EffectReconciliation.DisposeAsync("dispose-invalid", missing, (GovernedLoopEffectReconciliationDispositionKind)99)).Status);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.ListAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.ListProbeContractsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.ReadAsync(missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.ReadResolutionAsync(missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.ProbeAsync("probe-canceled", missing, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.DisposeAsync("dispose-canceled", missing, GovernedLoopEffectReconciliationDispositionKind.Unknown, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Effect_reconciliation_facade_projects_corrupt_canonical_case_evidence_without_a_partial_payload()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(workspace.RootPath, "corrupt-surface");
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider());
        var root = new WorkspacePaths(workspace.RootPath).GovernedLoopEffectAttemptsPath;
        var casePath = Assert.Single(Directory.EnumerateFiles(root, "reconciliation-case.*.json", SearchOption.TopDirectoryOnly));
        await File.WriteAllTextAsync(casePath, "{}");
        var reference = Reference(seeded.Current);

        var page = await runtime.EffectReconciliation.ListAsync();
        var read = await runtime.EffectReconciliation.ReadAsync(reference);
        var resolution = await runtime.EffectReconciliation.ReadResolutionAsync(reference);
        var operation = await runtime.EffectReconciliation.AssessAsync("assess-corrupt-surface", reference);

        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Corrupt, page.Status);
        Assert.Empty(page.Items);
        Assert.Equal(GovernedLoopEffectReconciliationReadStatus.Corrupt, read.Status);
        Assert.Null(read.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Corrupt, resolution.Status);
        Assert.Null(resolution.Resolution);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Corrupt, operation.Status);
        Assert.Null(operation.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_projects_shared_store_contention_as_unavailable_without_a_partial_payload()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(workspace.RootPath, "contended-surface");
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace);
        var root = new WorkspacePaths(workspace.RootPath).GovernedLoopEffectAttemptsPath;
        await using var mutationLease = new FileStream(Path.Combine(root, ".custom-loop-mutations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var reference = Reference(seeded.Current);

        var page = await runtime.EffectReconciliation.ListAsync();
        var read = await runtime.EffectReconciliation.ReadAsync(reference);
        var resolution = await runtime.EffectReconciliation.ReadResolutionAsync(reference);

        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Unavailable, page.Status);
        Assert.Empty(page.Items);
        Assert.Equal(GovernedLoopEffectReconciliationReadStatus.Unavailable, read.Status);
        Assert.Null(read.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Unavailable, resolution.Status);
        Assert.Null(resolution.Resolution);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_projects_exact_paged_immutable_cases_without_private_execution_or_authority_values()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var resolved = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(workspace.RootPath, "resolved-private", resolve: true);
        var open = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(workspace.RootPath, "open-private");
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace);

        var first = await runtime.EffectReconciliation.ListAsync(new GovernedLoopEffectReconciliationPageRequest(1));
        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Ready, first.Status);
        Assert.Single(first.Items);
        Assert.NotNull(first.NextCursor);
        var second = await runtime.EffectReconciliation.ListAsync(new GovernedLoopEffectReconciliationPageRequest(1, first.NextCursor));
        Assert.Equal(GovernedLoopEffectReconciliationPageStatus.Ready, second.Status);
        Assert.Single(second.Items);
        Assert.Null(second.NextCursor);
        Assert.Equal(2, first.Items.Concat(second.Items).Select(item => item.Reference.CaseId).Distinct(StringComparer.Ordinal).Count());

        var resolvedReference = Reference(resolved.Current);
        var openReference = Reference(open.Current);
        var read = await runtime.EffectReconciliation.ReadAsync(resolvedReference);
        var resolution = await runtime.EffectReconciliation.ReadResolutionAsync(resolvedReference);
        var openResolution = await runtime.EffectReconciliation.ReadResolutionAsync(openReference);

        Assert.Equal(GovernedLoopEffectReconciliationReadStatus.Found, read.Status);
        var detail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(read.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Resolved, detail.Posture);
        Assert.Equal(resolved.Current.CaseId, detail.Reference.CaseId);
        Assert.Equal(resolved.Current.CaseVersion, detail.Reference.CaseVersion);
        Assert.Equal(resolved.Current.ContentHash, detail.Reference.ContentHash);
        Assert.Equal(resolved.Current.Binding.ContentHash, detail.Reference.BindingHash);
        var source = Assert.Single(detail.EvidenceSources);
        Assert.Equal(GovernedLoopEffectReconciliationEvidenceSourceKind.Authoritative, source.Kind);
        Assert.Equal(GovernedLoopEffectReconciliationReliabilityPosture.Authoritative, source.ReliabilityPosture);
        var observation = Assert.Single(detail.Observations);
        Assert.Equal(GovernedLoopEffectReconciliationObservationKind.Evidence, observation.Kind);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.NotApplied, observation.ObservedOutcome);
        var assessment = Assert.Single(detail.Assessments);
        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.ProvedNotApplied, assessment.Kind);
        Assert.NotNull(detail.Disposition);
        Assert.NotNull(detail.Resolution);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.Found, resolution.Status);
        Assert.Equal(detail.Resolution, resolution.Resolution);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionReadStatus.NotFound, openResolution.Status);
        Assert.Null(openResolution.Resolution);

        var json = JsonSerializer.Serialize(detail);
        Assert.DoesNotContain(resolved.Attempt.Payload.OperationId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(resolved.Attempt.ActuatorOperationId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(resolved.Attempt.InputFingerprint, json, StringComparison.Ordinal);
        Assert.DoesNotContain("org.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-registration-authority-resolved-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private observation annotation resolved-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private assessment annotation resolved-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private disposition annotation resolved-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private resolution annotation resolved-private", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorityEvidence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OperationDescriptorHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ActuatorOperationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("RunId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("NodeId", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_projects_the_closed_evidence_and_assessment_vocabularies_without_private_annotations()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var timedOut = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "timed-out-projection",
            sourceKind: CommonReconciliationModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            reliabilityPosture: CommonReconciliationModels.GovernedLoopEffectReconciliationReliabilityPosture.Corroborating,
            observationKind: CommonReconciliationModels.GovernedLoopEffectReconciliationObservationKind.TimedOut,
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown);
        var cancelled = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "cancelled-projection",
            sourceKind: CommonReconciliationModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            reliabilityPosture: CommonReconciliationModels.GovernedLoopEffectReconciliationReliabilityPosture.Untrusted,
            observationKind: CommonReconciliationModels.GovernedLoopEffectReconciliationObservationKind.Cancelled,
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown);
        var prose = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "prose-projection",
            sourceKind: CommonReconciliationModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            reliabilityPosture: CommonReconciliationModels.GovernedLoopEffectReconciliationReliabilityPosture.Corroborating,
            observationKind: CommonReconciliationModels.GovernedLoopEffectReconciliationObservationKind.Prose,
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown);
        var callerAssertion = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "caller-assertion-projection",
            sourceKind: CommonReconciliationModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            reliabilityPosture: CommonReconciliationModels.GovernedLoopEffectReconciliationReliabilityPosture.Untrusted,
            observationKind: CommonReconciliationModels.GovernedLoopEffectReconciliationObservationKind.CallerAssertion,
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown);
        var unprovenHash = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "unproven-hash-projection",
            sourceKind: CommonReconciliationModels.GovernedLoopEffectReconciliationEvidenceSourceKind.Informational,
            reliabilityPosture: CommonReconciliationModels.GovernedLoopEffectReconciliationReliabilityPosture.Corroborating,
            observationKind: CommonReconciliationModels.GovernedLoopEffectReconciliationObservationKind.UnprovenHash,
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.Unknown);
        var failed = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "failed-assessment-projection",
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed,
            assessmentKind: CommonReconciliationModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed);
        var outcomeUnknown = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "outcome-unknown-assessment-projection",
            observedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown,
            assessmentKind: CommonReconciliationModels.GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown);
        var conflicting = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(
            workspace.RootPath,
            "conflicting-assessment-projection",
            secondaryObservedOutcome: CommonReconciliationModels.GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded,
            assessmentKind: CommonReconciliationModels.GovernedLoopEffectReconciliationAssessmentKind.Conflicting);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace);

        var details = new[]
        {
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(timedOut.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(cancelled.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(prose.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(callerAssertion.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(unprovenHash.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(failed.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(outcomeUnknown.Current))).Detail),
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>((await runtime.EffectReconciliation.ReadAsync(Reference(conflicting.Current))).Detail),
        };

        Assert.Equal(
            [
                GovernedLoopEffectReconciliationObservationKind.TimedOut,
                GovernedLoopEffectReconciliationObservationKind.Cancelled,
                GovernedLoopEffectReconciliationObservationKind.Prose,
                GovernedLoopEffectReconciliationObservationKind.CallerAssertion,
                GovernedLoopEffectReconciliationObservationKind.UnprovenHash,
            ],
            details.Take(5).Select(detail => Assert.Single(detail.Observations).Kind));
        Assert.Equal(GovernedLoopEffectReconciliationEvidenceSourceKind.Informational, Assert.Single(details[0].EvidenceSources).Kind);
        Assert.Equal(GovernedLoopEffectReconciliationReliabilityPosture.Corroborating, Assert.Single(details[0].EvidenceSources).ReliabilityPosture);
        Assert.Equal(GovernedLoopEffectReconciliationReliabilityPosture.Untrusted, Assert.Single(details[1].EvidenceSources).ReliabilityPosture);
        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed, Assert.Single(details[5].Assessments).Kind);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.AppliedOutcomeUnknown, Assert.Single(details[6].Observations).ObservedOutcome);
        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedOutcomeUnknown, Assert.Single(details[6].Assessments).Kind);
        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.Conflicting, Assert.Single(details[7].Assessments).Kind);
        Assert.Equal(2, details[7].Observations.Count);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(details), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_binds_every_operation_to_request_scoped_surface_authority_and_fails_closed()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationStartupTestFixture.SeedAsync(workspace.RootPath, "authority");
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider();
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider);
        var reference = Reference(seeded.Current);

        foreach (var (status, expected) in new[]
        {
            (GovernedLoopEffectReconciliationAuthorizationStatus.Denied, GovernedLoopEffectReconciliationOperationStatus.Denied),
            (GovernedLoopEffectReconciliationAuthorizationStatus.Invalid, GovernedLoopEffectReconciliationOperationStatus.Invalid),
            (GovernedLoopEffectReconciliationAuthorizationStatus.Corrupt, GovernedLoopEffectReconciliationOperationStatus.Corrupt),
            (GovernedLoopEffectReconciliationAuthorizationStatus.Unavailable, GovernedLoopEffectReconciliationOperationStatus.Unavailable),
        })
        {
            provider.Status = status;
            var result = await runtime.EffectReconciliation.AssessAsync("assess-authority-" + status.ToString().ToLowerInvariant(), reference);
            Assert.Equal(expected, result.Status);
            Assert.Null(result.Detail);
        }

        provider.Status = GovernedLoopEffectReconciliationAuthorizationStatus.Ready;
        provider.MismatchRequestHash = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Corrupt, (await runtime.EffectReconciliation.AssessAsync("assess-authority-mismatch", reference)).Status);
        provider.MismatchRequestHash = false;
        provider.Throw = true;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await runtime.EffectReconciliation.AssessAsync("assess-authority-throw", reference)).Status);
        provider.Throw = false;
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, (await runtime.EffectReconciliation.AssessAsync("assess-authority-ready", reference)).Status);

        using var cancellation = new CancellationTokenSource();
        provider.OnCall = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.EffectReconciliation.AssessAsync("assess-authority-canceled", reference, cancellationToken: cancellation.Token));
        provider.OnCall = null;

        var request = Assert.IsType<GovernedLoopEffectReconciliationAuthorizationRequest>(provider.LastRequest);
        Assert.Equal(CapabilityWorkspaceScopeId.Create(workspace.RootPath), request.WorkspaceId);
        Assert.Equal(AgentRuntimeSurface.Web.Id, request.SurfaceId);
        Assert.Equal("effect-reconciliation", request.Purpose);
        Assert.Equal(reference, request.Case);
        Assert.Equal(64, request.RequestHash.Length);
        Assert.True(provider.Calls >= 7);
        Assert.Equal(GovernedLoopEffectReconciliationReadStatus.Found, (await runtime.EffectReconciliation.ReadAsync(reference)).Status);

        await using var unavailableRuntime = await CreateEffectReconciliationRuntimeAsync(workspace);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Unavailable, (await unavailableRuntime.EffectReconciliation.AssessAsync("assess-no-authority", reference)).Status);
    }

    [Fact]
    public async Task Effect_reconciliation_probe_catalog_projects_only_value_free_registered_contract_identity()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var registration = GovernedCommandActionFactoryTests.TypedRegistration();
        var commandProvider = new CommandActionRuntimeProvider(
            [registration],
            DenyingCapabilityExecutableArtifactResolver.Instance,
            AvailableCommandActionProcessIsolationBoundary.Instance);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, commandActionRuntimeProvider: commandProvider);

        var catalog = await runtime.EffectReconciliation.ListProbeContractsAsync();
        var invalid = await runtime.EffectReconciliation.ListProbeContractsAsync(new GovernedLoopEffectReconciliationPageRequest(1, "foreign-cursor"));
        var outOfRange = await runtime.EffectReconciliation.ListProbeContractsAsync(new GovernedLoopEffectReconciliationPageRequest(1, "reconciliation-probe-cursor-v1-2"));
        var nonCanonical = await runtime.EffectReconciliation.ListProbeContractsAsync(new GovernedLoopEffectReconciliationPageRequest(1, "reconciliation-probe-cursor-v1-01"));

        Assert.Equal(GovernedLoopEffectReconciliationProbeCatalogStatus.Ready, catalog.Status);
        var contract = Assert.Single(catalog.Contracts);
        Assert.StartsWith("command-reconciliation-", contract.ContractId, StringComparison.Ordinal);
        Assert.StartsWith("command-outcome-probe-", contract.ProbeContractId, StringComparison.Ordinal);
        Assert.Equal(1, contract.ContractVersion);
        Assert.Equal(1, contract.ProbeContractVersion);
        Assert.Equal(64, contract.ContractHash.Length);
        Assert.Equal(64, contract.ProbeContractHash.Length);
        Assert.Equal(GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, invalid.Status);
        Assert.Empty(invalid.Contracts);
        Assert.Null(invalid.NextCursor);
        Assert.Equal(GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, outOfRange.Status);
        Assert.Equal(GovernedLoopEffectReconciliationProbeCatalogStatus.Invalid, nonCanonical.Status);

        var json = JsonSerializer.Serialize(catalog);
        Assert.DoesNotContain("org.example", json, StringComparison.Ordinal);
        Assert.DoesNotContain("command/runner", json, StringComparison.Ordinal);
        Assert.DoesNotContain("command/render", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OperationDescriptorHash", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ActuatorOperationId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Implementation", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Capability", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeTarget", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_applies_and_replays_assessment_and_disposition_then_publishes_resolution()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider();
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);
        var openReference = Reference(seeded.Case);
        var probed = await runtime.EffectReconciliation.ProbeAsync("probe-surface-chain", openReference);
        var probedDetail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(probed.Detail);

        var assessed = await runtime.EffectReconciliation.AssessAsync("assess-surface-chain", probedDetail.Reference, "private assessment surface detail");
        var assessedReplay = await runtime.EffectReconciliation.AssessAsync("assess-surface-chain", probedDetail.Reference, "private assessment surface detail");
        var assessedConflict = await runtime.EffectReconciliation.AssessAsync("assess-surface-chain", probedDetail.Reference, "different private assessment surface detail");

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, assessed.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, assessedReplay.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, assessedConflict.Status);
        var assessedDetail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(assessed.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Assessed, assessedDetail.Posture);
        Assert.Single(assessedDetail.Assessments);
        Assert.Null(assessedDetail.Disposition);
        Assert.Null(assessedDetail.Resolution);
        var assessedSummary = Assert.Single((await runtime.EffectReconciliation.ListAsync()).Items, value => value.Reference.CaseId == assessedDetail.Reference.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Assessed, assessedSummary.Posture);
        Assert.Equal(
            GovernedLoopEffectReconciliationOperationStatus.Invalid,
            (await runtime.EffectReconciliation.DisposeAsync("dispose-surface-chain-mismatch", assessedDetail.Reference, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied)).Status);

        var disposed = await runtime.EffectReconciliation.DisposeAsync(
            "dispose-surface-chain",
            assessedDetail.Reference,
            GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied,
            "private disposition surface detail");
        var disposedReplay = await runtime.EffectReconciliation.DisposeAsync(
            "dispose-surface-chain",
            assessedDetail.Reference,
            GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied,
            "private disposition surface detail");

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, disposed.Status);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, disposedReplay.Status);
        var disposedDetail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(disposed.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Accepted, disposedDetail.Posture);
        Assert.Equal(GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied, disposedDetail.Disposition?.Kind);
        Assert.Null(disposedDetail.Resolution);
        var disposedSummary = Assert.Single((await runtime.EffectReconciliation.ListAsync()).Items, value => value.Reference.CaseId == disposedDetail.Reference.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Accepted, disposedSummary.Posture);

        var resolved = await runtime.EffectReconciliation.ResolveAsync("resolve-surface-chain", disposedDetail.Reference, "private resolution surface detail");

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, resolved.Status);
        var resolvedDetail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(resolved.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Resolved, resolvedDetail.Posture);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionOutcome.Succeeded, resolvedDetail.Resolution?.Outcome);
        var resolvedSummary = Assert.Single((await runtime.EffectReconciliation.ListAsync()).Items, value => value.Reference.CaseId == resolvedDetail.Reference.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Resolved, resolvedSummary.Posture);

        var json = JsonSerializer.Serialize(new[] { assessed, disposed, resolved });
        Assert.DoesNotContain("private assessment surface detail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private disposition surface detail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private resolution surface detail", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AuthorityEvidence", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_invokes_one_registered_read_only_command_probe_and_replays_exactly()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider();
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);
        var reference = Reference(seeded.Case);

        var applied = await runtime.EffectReconciliation.ProbeAsync("probe-command-surface", reference);
        var replayed = await runtime.EffectReconciliation.ProbeAsync("probe-command-surface", reference);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, applied.Status);
        var detail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(applied.Detail);
        Assert.Equal(seeded.Case.CaseVersion + 1, detail.Reference.CaseVersion);
        var observation = Assert.Single(detail.Observations);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.AppliedSucceeded, observation.ObservedOutcome);
        Assert.StartsWith("command-outcome-", observation.EvidenceReference, StringComparison.Ordinal);
        Assert.Equal(64, Assert.IsType<string>(observation.EvidenceHash).Length);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.Equal(JsonSerializer.Serialize(detail), JsonSerializer.Serialize(replayed.Detail));
        Assert.Equal(3, provider.Calls);

        var json = JsonSerializer.Serialize(applied);
        Assert.DoesNotContain(seeded.Attempt.Payload.OperationId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Attempt.ActuatorOperationId, json, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Attempt.InputFingerprint, json, StringComparison.Ordinal);
        Assert.DoesNotContain("WorkspaceId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProbeTarget", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_retains_an_explicit_unknown_observation_when_the_registered_probe_has_no_outcome()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, retainOutcome: false);
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider();
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);
        var reference = Reference(seeded.Case);

        var applied = await runtime.EffectReconciliation.ProbeAsync("probe-command-missing-outcome", reference);
        var replayed = await runtime.EffectReconciliation.ProbeAsync("probe-command-missing-outcome", reference);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, applied.Status);
        var detail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(applied.Detail);
        var observation = Assert.Single(detail.Observations);
        Assert.Equal(GovernedLoopEffectReconciliationObservationKind.Missing, observation.Kind);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.Unknown, observation.ObservedOutcome);
        Assert.Null(observation.EvidenceReference);
        Assert.Null(observation.EvidenceHash);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Replayed, replayed.Status);
        Assert.Equal(JsonSerializer.Serialize(detail), JsonSerializer.Serialize(replayed.Detail));
    }

    [Fact]
    public async Task Effect_reconciliation_facade_projects_a_conclusive_failed_command_outcome()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, outcomeKind: EmbodySense.Core.Common.CommandActions.Models.CommandActionOutcomeKind.NonZeroExit);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(), seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-failed-outcome", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, result.Status);
        var probed = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(result.Detail);
        var observation = Assert.Single(probed.Observations);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.AppliedFailed, observation.ObservedOutcome);
        Assert.StartsWith("command-outcome-", observation.EvidenceReference, StringComparison.Ordinal);
        var assessed = await runtime.EffectReconciliation.AssessAsync("assess-command-failed-outcome", probed.Reference);
        var assessedDetail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(assessed.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationAssessmentKind.ProvedAppliedFailed, Assert.Single(assessedDetail.Assessments).Kind);
        var disposed = await runtime.EffectReconciliation.DisposeAsync("dispose-command-failed-outcome", assessedDetail.Reference, GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied);
        var disposedDetail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(disposed.Detail);
        var resolved = await runtime.EffectReconciliation.ResolveAsync("resolve-command-failed-outcome", disposedDetail.Reference);
        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, resolved.Status);
        Assert.Equal(GovernedLoopEffectReconciliationResolutionOutcome.Failed, Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(resolved.Detail).Resolution?.Outcome);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_projects_an_explicit_quarantine_without_resolution()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace, retainOutcome: false);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(), seeded.RuntimeProvider);
        var probed = await runtime.EffectReconciliation.ProbeAsync("probe-command-quarantine", Reference(seeded.Case));
        var assessed = await runtime.EffectReconciliation.AssessAsync("assess-command-quarantine", Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(probed.Detail).Reference);

        var quarantined = await runtime.EffectReconciliation.DisposeAsync(
            "dispose-command-quarantine",
            Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(assessed.Detail).Reference,
            GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved);

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, quarantined.Status);
        var detail = Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(quarantined.Detail);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Quarantined, detail.Posture);
        Assert.Equal(GovernedLoopEffectReconciliationDispositionKind.QuarantineUnresolved, detail.Disposition?.Kind);
        Assert.Null(detail.Resolution);
        var summary = Assert.Single((await runtime.EffectReconciliation.ListAsync()).Items, value => value.Reference.CaseId == detail.Reference.CaseId);
        Assert.Equal(GovernedLoopEffectReconciliationCasePosture.Quarantined, summary.Posture);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_fails_closed_when_the_canonical_command_run_is_missing()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var runPath = Assert.Single(Directory.EnumerateFiles(new WorkspacePaths(workspace.RootPath).CustomLoopRunsPath, "run-reconciliation-command.json", SearchOption.AllDirectories));
        File.Delete(runPath);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(), seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-missing-run", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.NotFound, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_fails_closed_when_the_canonical_command_run_is_corrupt()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var runPath = Assert.Single(Directory.EnumerateFiles(new WorkspacePaths(workspace.RootPath).CustomLoopRunsPath, "run-reconciliation-command.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(runPath, "{}");
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(), seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-corrupt-run", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Corrupt, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_fails_closed_when_the_exact_command_registration_is_absent()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider());

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-missing-registration", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Conflict, result.Status);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Effect_reconciliation_facade_fails_closed_when_runtime_input_no_longer_matches_the_exact_case_binding(
        bool mismatchRunNodeBinding,
        bool mismatchCommandParameters)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(
            workspace,
            mismatchRunNodeBinding: mismatchRunNodeBinding,
            mismatchCommandParameters: mismatchCommandParameters);
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(), seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-mismatched-input-" + mismatchRunNodeBinding + mismatchCommandParameters, Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Invalid, result.Status);
        Assert.Null(result.Detail);
    }

    [Theory]
    [InlineData(false, GovernedLoopEffectReconciliationOperationStatus.Corrupt)]
    [InlineData(true, GovernedLoopEffectReconciliationOperationStatus.Corrupt)]
    public async Task Effect_reconciliation_facade_fails_closed_when_the_pinned_graph_artifact_is_missing_or_corrupt(
        bool corrupt,
        GovernedLoopEffectReconciliationOperationStatus expected)
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var paths = new WorkspacePaths(workspace.RootPath);
        var revision = seeded.Case.Binding.Execution.Revision;
        var artifactPath = Path.Combine(paths.AgentPath, "loops", "revisions", "graph-authoring", "artifacts", revision.GraphId, revision.RevisionId + ".json");
        if (corrupt)
        {
            await File.WriteAllTextAsync(artifactPath, "{}");
        }
        else
        {
            File.Delete(artifactPath);
        }
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, new RecordingGovernedLoopEffectReconciliationAuthorizationProvider(), seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-graph-closed-" + corrupt.ToString().ToLowerInvariant(), Reference(seeded.Case));

        Assert.Equal(expected, result.Status);
        Assert.Null(result.Detail);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_records_unknown_when_the_run_disappears_at_the_callback_boundary()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var runPath = Assert.Single(Directory.EnumerateFiles(new WorkspacePaths(workspace.RootPath).CustomLoopRunsPath, "run-reconciliation-command.json", SearchOption.AllDirectories));
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider
        {
            OnCall = call =>
            {
                if (call == 2)
                {
                    File.Delete(runPath);
                }
            }
        };
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-run-lost-at-callback", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, result.Status);
        var observation = Assert.Single(Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(result.Detail).Observations);
        Assert.Equal(GovernedLoopEffectReconciliationObservationKind.Missing, observation.Kind);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.Unknown, observation.ObservedOutcome);
    }

    [Fact]
    public async Task Effect_reconciliation_facade_records_unknown_when_command_evidence_becomes_corrupt_at_the_callback_boundary()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var seeded = await GovernedLoopEffectReconciliationCommandStartupTestFixture.SeedAsync(workspace);
        var outcomeRoot = Path.Combine(new WorkspacePaths(workspace.RootPath).AgentPath, "loops", "execution", "command-actions", "outcomes");
        var outcomePath = Assert.Single(Directory.EnumerateFiles(outcomeRoot, "command-outcome-*.json", SearchOption.TopDirectoryOnly));
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider
        {
            OnCall = call =>
            {
                if (call == 2)
                {
                    File.WriteAllText(outcomePath, "{}");
                }
            }
        };
        await using var runtime = await CreateEffectReconciliationRuntimeAsync(workspace, provider, seeded.RuntimeProvider);

        var result = await runtime.EffectReconciliation.ProbeAsync("probe-command-corrupt-evidence-at-callback", Reference(seeded.Case));

        Assert.Equal(GovernedLoopEffectReconciliationOperationStatus.Applied, result.Status);
        var observation = Assert.Single(Assert.IsType<GovernedLoopEffectReconciliationCaseDetail>(result.Detail).Observations);
        Assert.Equal(GovernedLoopEffectReconciliationObservationKind.Missing, observation.Kind);
        Assert.Equal(GovernedLoopEffectReconciliationObservedOutcome.Unknown, observation.ObservedOutcome);
    }

    [Fact]
    public async Task Effect_reconciliation_factory_clone_is_immutable_and_rejects_a_missing_authority_provider()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(new RejectingApprovalPrompt(), workspace.ServerStatePath, CreateCompatibleRuntimeStatus(executablePath));
        var provider = new RecordingGovernedLoopEffectReconciliationAuthorizationProvider();

        Assert.Throws<ArgumentNullException>(() => factory.WithGovernedLoopEffectReconciliationAuthorizationProvider(null!));
        var configured = factory.WithGovernedLoopEffectReconciliationAuthorizationProvider(provider);
        Assert.NotSame(factory, configured);

        await using var runtime = await configured.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
        Assert.NotNull(runtime.EffectReconciliation);
    }

    private static GovernedLoopEffectReconciliationCaseReference Reference(string suffix)
        => new("case-reconciliation-" + suffix, 1, GovernedLoopEffectReconciliationStartupTestFixture.Hash("case-" + suffix), GovernedLoopEffectReconciliationStartupTestFixture.Hash("binding-" + suffix));

    private static GovernedLoopEffectReconciliationCaseReference Reference(EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models.GovernedLoopEffectReconciliationCase value)
        => new(value.CaseId, value.CaseVersion, value.ContentHash, value.Binding.ContentHash);

    private static async Task<AgentRuntime> CreateEffectReconciliationRuntimeAsync(
        TestWorkspace workspace,
        IGovernedLoopEffectReconciliationAuthorizationProvider? authorizationProvider = null,
        CommandActionRuntimeProvider? commandActionRuntimeProvider = null)
    {
        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        var factory = AgentRuntimeFactory.ForFileCapabilityTrustRoot(
            new RejectingApprovalPrompt(),
            workspace.ServerStatePath,
            CreateCompatibleRuntimeStatus(executablePath),
            commandActionRuntimeProvider: commandActionRuntimeProvider);
        if (authorizationProvider is not null)
        {
            factory = factory.WithGovernedLoopEffectReconciliationAuthorizationProvider(authorizationProvider);
        }
        return await factory.CreateAsync("test-model", workspace.RootPath, executablePath, "read-only", AgentRuntimeSurface.Web);
    }
}
