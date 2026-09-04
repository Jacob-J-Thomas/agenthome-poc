using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Startup.HumanReview.Models;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Tests.Support;
using CommonHumanReviewRequest = EmbodySense.Core.Common.HumanReview.Models.HumanReviewRequest;
using CommonHumanReviewPurpose = EmbodySense.Core.Common.HumanReview.Models.HumanReviewPurpose;
using CommonHumanReviewTiming = EmbodySense.Core.Common.HumanReview.Models.HumanReviewTiming;
using StartupEffectCertainty = EmbodySense.Core.Startup.HumanReview.Models.HumanReviewEffectCertainty;

using static EmbodySense.Core.Startup.Tests.Runtime.AgentRuntimeFactoryTests;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed partial class AgentRuntimeHumanReviewTests
{
    [Fact]
    public async Task Public_human_review_facade_projects_each_canonical_effect_certainty_posture()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var workspaceId = CapabilityWorkspaceScopeId.Create(workspace.RootPath);
        var exact = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-exact", workspaceId);
        var stale = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-stale", workspaceId);
        var dispatched = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-dispatched", workspaceId);
        var conclusive = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-conclusive", workspaceId);
        var ambiguous = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-ambiguous", workspaceId);
        var terminal = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-terminal", workspaceId);

        stale.Attempt = await AttachAuthorityAsync(stale.Store, stale.Attempt);
        dispatched.Attempt = await AdvanceToDispatchAsync(dispatched.Store, dispatched.Attempt);
        conclusive.Attempt = await AdvanceToConclusiveAsync(conclusive.Store, conclusive.Attempt);
        ambiguous.Attempt = await AdvanceToAmbiguousAsync(ambiguous.Store, ambiguous.Attempt);
        terminal.Attempt = await AdvanceToTerminalAsync(terminal.Store, terminal.Attempt);

        var directProbe = await new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspace.RootPath)).ReadAsync(
            workspaceId,
            operationId: exact.Attempt.Payload.OperationId,
            effectGeneration: exact.Attempt.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptReadStatus.Current, directProbe.Status);

        var executablePath = await CreateFakeCodexExecutableAsync(workspace);
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web, executablePath);

        await AssertEffectPostureAsync(runtime, exact.Run.Id, HumanReviewEffectEvidenceStatus.ExactNotStarted, StartupEffectCertainty.NotStarted);
        await AssertEffectPostureAsync(runtime, stale.Run.Id, HumanReviewEffectEvidenceStatus.Stale, StartupEffectCertainty.NotStarted);
        await AssertEffectPostureAsync(runtime, dispatched.Run.Id, HumanReviewEffectEvidenceStatus.Dispatched, StartupEffectCertainty.Dispatched);
        await AssertEffectPostureAsync(runtime, conclusive.Run.Id, HumanReviewEffectEvidenceStatus.Conclusive, StartupEffectCertainty.Conclusive);
        await AssertEffectPostureAsync(runtime, ambiguous.Run.Id, HumanReviewEffectEvidenceStatus.Ambiguous, StartupEffectCertainty.Ambiguous);
        await AssertEffectPostureAsync(runtime, terminal.Run.Id, HumanReviewEffectEvidenceStatus.Terminal, StartupEffectCertainty.Terminal);
    }

    [Fact]
    public async Task Public_human_review_facade_projects_canonical_effect_corruption_as_corrupt_without_values()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);

        var scenario = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-corrupt", CapabilityWorkspaceScopeId.Create(workspace.RootPath));
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(Path.Combine(paths.GovernedLoopEffectAttemptsPath, "unsupported-effect-artifact"), "corrupt");

        var detail = await runtime.HumanReview.ReadAsync(scenario.Run.Id);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(scenario.Run.Id);

        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Corrupt, detail.Detail!.EffectEvidence!.Status);
        Assert.Equal(HumanReviewEvidenceReadStatus.Ready, evidence.Status);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Corrupt, evidence.EffectEvidence!.Status);
        Assert.Null(detail.Detail.EffectEvidence.Certainty);
        Assert.Null(detail.Detail.EffectEvidence.IdentityHash);
        Assert.Null(detail.Detail.EffectEvidence.PreparationHash);
    }

    [Fact]
    public async Task Public_human_review_facade_projects_missing_effect_evidence_closed()
    {
        using var missingWorkspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(missingWorkspace.ServerStatePath).InitializeAsync(missingWorkspace.RootPath);
        var missing = await SeedPreDispatchEffectAsync(missingWorkspace, "run-public-effect-missing", CapabilityWorkspaceScopeId.Create(missingWorkspace.RootPath));
        foreach (var path in Directory.EnumerateFiles(new WorkspacePaths(missingWorkspace.RootPath).GovernedLoopEffectAttemptsPath)
            .Where(path => !string.Equals(Path.GetFileName(path), ".custom-loop-mutations.lock", StringComparison.Ordinal)).ToArray())
        {
            File.Delete(path);
        }

        await using var missingRuntime = await CreateRuntimeAsync(missingWorkspace, AgentRuntimeSurface.Web);
        var missingDetail = await missingRuntime.HumanReview.ReadAsync(missing.Run.Id);
        var missingEvidence = await missingRuntime.HumanReview.ReadEvidenceAsync(missing.Run.Id);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Missing, missingDetail.Detail!.EffectEvidence!.Status);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Missing, missingEvidence.EffectEvidence!.Status);
        Assert.Null(missingDetail.Detail.EffectEvidence.Certainty);
        Assert.Null(missingDetail.Detail.EffectEvidence.IdentityHash);
        Assert.Null(missingDetail.Detail.EffectEvidence.PreparationHash);

    }

    [Fact]
    public async Task Public_human_review_facade_projects_locked_effect_evidence_as_unavailable_without_values()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var scenario = await SeedPreDispatchEffectAsync(workspace, "run-public-effect-locked", CapabilityWorkspaceScopeId.Create(workspace.RootPath));
        await using var runtime = await CreateRuntimeAsync(workspace, AgentRuntimeSurface.Web);

        var lockPath = Path.Combine(new WorkspacePaths(workspace.RootPath).GovernedLoopEffectAttemptsPath, ".custom-loop-mutations.lock");
        using var lockHandle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
        var detail = await runtime.HumanReview.ReadAsync(scenario.Run.Id);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(scenario.Run.Id);

        Assert.Equal(HumanReviewEffectEvidenceStatus.Unavailable, detail.Detail!.EffectEvidence!.Status);
        Assert.Equal(HumanReviewEffectEvidenceStatus.Unavailable, evidence.EffectEvidence!.Status);
        Assert.Null(detail.Detail.EffectEvidence.Certainty);
        Assert.Null(detail.Detail.EffectEvidence.IdentityHash);
        Assert.Null(detail.Detail.EffectEvidence.PreparationHash);
    }

    private static async Task AssertEffectPostureAsync(AgentRuntime runtime, string runId, HumanReviewEffectEvidenceStatus expectedStatus, StartupEffectCertainty? expectedCertainty)
    {
        var detail = await runtime.HumanReview.ReadAsync(runId);
        var evidence = await runtime.HumanReview.ReadEvidenceAsync(runId);
        var projection = Assert.IsType<HumanReviewDetail>(detail.Detail);
        var effect = Assert.IsType<HumanReviewEffectEvidence>(projection.EffectEvidence);

        Assert.Equal(HumanReviewReadStatus.Ready, detail.Status);
        Assert.Equal(expectedStatus, effect.Status);
        Assert.Equal(expectedCertainty, effect.Certainty);
        Assert.Equal(HumanReviewEvidenceReadStatus.Ready, evidence.Status);
        Assert.Equal(expectedStatus, evidence.EffectEvidence!.Status);
        Assert.Equal(expectedCertainty, evidence.EffectEvidence.Certainty);
        Assert.NotNull(effect.EffectAttemptId);
        if (expectedStatus is HumanReviewEffectEvidenceStatus.ExactNotStarted or HumanReviewEffectEvidenceStatus.Dispatched or HumanReviewEffectEvidenceStatus.Conclusive or HumanReviewEffectEvidenceStatus.Ambiguous or HumanReviewEffectEvidenceStatus.Terminal)
        {
            Assert.Matches("^[a-f0-9]{64}$", effect.IdentityHash!);
            Assert.Matches("^[a-f0-9]{64}$", effect.PreparationHash!);
        }
    }

    private static async Task<(CustomLoopRunRecord Run, GovernedLoopEffectAttempt Attempt, GovernedLoopEffectAttemptStore Store)> SeedPreDispatchEffectAsync(TestWorkspace workspace, string runId, string workspaceId)
    {
        var blueprint = await HumanReviewRecoveryCanonicalRunFactory.CreateApprovedRunAsync(runId, "admission-" + runId, "sequential-loop-" + runId, workspaceId: workspaceId);
        var request = blueprint.HumanReview!.Request;
        var now = DateTimeOffset.UtcNow;
        var timing = new CommonHumanReviewTiming(request.Timing.CreatedAtUtc, now, now.AddHours(1));
        blueprint = blueprint with { HumanReview = blueprint.HumanReview with { Request = HumanReviewContractHash.ApplyRequest(request with { Timing = timing }) } };
        request = CreatePreDispatchRequestWithEffect(blueprint, out var attempt);
        blueprint = blueprint with { HumanReview = blueprint.HumanReview! with { Request = request } };
        await PersistPendingHumanReviewAsync(workspace, blueprint);

        var store = new GovernedLoopEffectAttemptStore(new WorkspacePaths(workspace.RootPath));
        var created = await store.BeginAsync(attempt);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, created.Status);
        created.Lease?.Dispose();
        using var run = new CustomLoopRunStore(new WorkspacePaths(workspace.RootPath));
        var persisted = await run.GetAsync(runId);
        Assert.NotNull(persisted);
        return (persisted!, attempt, store);
    }

    private static CommonHumanReviewRequest CreatePreDispatchRequestWithEffect(CustomLoopRunRecord blueprint, out GovernedLoopEffectAttempt attempt)
    {
        var template = GovernedLoopEffectAttemptTestFixture.Create();
        var review = blueprint.HumanReview ?? throw new InvalidOperationException("The canonical review blueprint did not contain Human Review state.");
        var binding = review.Request.Binding;
        var adapter = blueprint.SequentialAdapterBinding ?? throw new InvalidOperationException("The canonical review blueprint did not contain an execution binding.");
        var activation = blueprint.Frontier?.Payload.Nodes.Single(node => node.Status == GovernedLoopNodeExecutionStatus.ReviewBlocked)
            ?? throw new InvalidOperationException("The canonical review blueprint did not contain a blocked review activation.");
        const string EffectId = "effect-public-facade";
        const string OperationId = "effect-public-facade-operation";
        attempt = GovernedLoopEffectAttemptContract.Prepare(
            adapter.ExecutionBinding,
            activation.NodeId,
            activation.Attempt!.Value,
            template.Request.CapabilityPin.DescriptorIdentity,
            template.Request.CapabilityPin.Implementation,
            template.Request.ActuatorOperationId,
            template.Descriptor.ContentHash,
            EffectId,
            OperationId + "-" + blueprint.Id,
            1,
            GovernedLoopEffectAttemptTestFixture.HashInput("input:" + blueprint.Id),
            GovernedLoopEffectAttemptTestFixture.HashInput("target:" + blueprint.Id),
            GovernedLoopEffectAttemptTestFixture.Hash('e'),
            adapter.AdmissionReceipt.ContentHash,
            "before-" + GovernedLoopEffectAttemptTestFixture.Hash('d'),
            blueprint.UpdatedAtUtc);
        var identity = HumanReviewEffectReleaseContract.CreateIdentity(binding, attempt);
        var preparation = HumanReviewEffectReleaseContract.CreatePreparation(binding, attempt);
        var reviewed = HumanReviewContractHash.ApplyEffectAttempt(new HumanReviewEffectAttemptBinding(
            EffectId,
            attempt.Payload.OperationId,
            attempt.Payload.EffectGeneration,
            identity.IntentHash,
            preparation.PreparationHash,
            HumanReviewEffectDispatchCertainty.NotDispatched,
            string.Empty));
        var reviewedBinding = HumanReviewContractHash.ApplyBinding(binding with { EffectAttempt = reviewed });
        var scope = HumanReviewContractHash.ApplyApprovalScope(review.Request.ApprovalScope with
        {
            Kind = HumanReviewApprovalScopeKind.PreDispatchEffect,
            BindingHash = reviewedBinding.BindingHash,
            EffectAttemptId = EffectId
        });
        return HumanReviewContractHash.ApplyRequest(review.Request with
        {
            Binding = reviewedBinding,
            Purpose = CommonHumanReviewPurpose.PreDispatchEffect,
            ApprovalScope = scope
        });
    }

    private static Task<GovernedLoopEffectAttempt> AttachAuthorityAsync(GovernedLoopEffectAttemptStore store, GovernedLoopEffectAttempt current)
        => ReplaceAttemptAsync(store, current, attempt => GovernedLoopEffectAttemptContract.AttachDispatchAuthority(attempt, GovernedLoopEffectAttemptTestFixture.Hash('9'), attempt.Payload.UpdatedAtUtc.AddSeconds(1)));

    private static async Task<GovernedLoopEffectAttempt> AdvanceToDispatchAsync(GovernedLoopEffectAttemptStore store, GovernedLoopEffectAttempt current)
    {
        current = await AttachAuthorityAsync(store, current);
        return await ReplaceAttemptAsync(store, current, attempt => GovernedLoopEffectAttemptContract.Advance(attempt, GovernedLoopEffectPhase.DispatchBoundaryReached, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Pending, null, null, attempt.Payload.UpdatedAtUtc.AddSeconds(1)));
    }

    private static async Task<GovernedLoopEffectAttempt> AdvanceToConclusiveAsync(GovernedLoopEffectAttemptStore store, GovernedLoopEffectAttempt current)
    {
        current = await AdvanceToDispatchAsync(store, current);
        return await ReplaceAttemptAsync(store, current, attempt => GovernedLoopEffectAttemptContract.Advance(attempt, GovernedLoopEffectPhase.OutcomeObserved, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, "outcome-" + attempt.Payload.OperationId, "after-" + attempt.Payload.OperationId, attempt.Payload.UpdatedAtUtc.AddSeconds(1)));
    }

    private static async Task<GovernedLoopEffectAttempt> AdvanceToAmbiguousAsync(GovernedLoopEffectAttemptStore store, GovernedLoopEffectAttempt current)
    {
        current = await AdvanceToDispatchAsync(store, current);
        return await ReplaceAttemptAsync(store, current, attempt => GovernedLoopEffectAttemptContract.Advance(attempt, GovernedLoopEffectPhase.ReconciliationRequired, GovernedLoopEffectOutcome.OutcomeUnknown, GovernedLoopEffectEvidenceStatus.Incomplete, null, null, attempt.Payload.UpdatedAtUtc.AddSeconds(1)));
    }

    private static async Task<GovernedLoopEffectAttempt> AdvanceToTerminalAsync(GovernedLoopEffectAttemptStore store, GovernedLoopEffectAttempt current)
    {
        current = await AdvanceToConclusiveAsync(store, current);
        return await ReplaceAttemptAsync(store, current, attempt => GovernedLoopEffectAttemptContract.Advance(attempt, GovernedLoopEffectPhase.Committed, GovernedLoopEffectOutcome.Succeeded, GovernedLoopEffectEvidenceStatus.Complete, attempt.Payload.OutcomeEvidenceId, attempt.AfterEvidenceId, attempt.Payload.UpdatedAtUtc.AddSeconds(1)));
    }

    private static async Task<GovernedLoopEffectAttempt> ReplaceAttemptAsync(GovernedLoopEffectAttemptStore store, GovernedLoopEffectAttempt current, Func<GovernedLoopEffectAttempt, GovernedLoopEffectAttempt> successor)
    {
        var resumed = await store.ResumeAsync(current.Payload.OperationId, current.Payload.EffectGeneration);
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Replayed, resumed.Status);
        Assert.NotNull(resumed.Lease);
        var next = successor(current);
        var saved = await store.CompareExchangeAsync(current.ContentHash, next, resumed.Lease!);
        resumed.Lease.Dispose();
        Assert.Equal(GovernedLoopEffectAttemptStoreStatus.Created, saved.Status);
        return Assert.IsType<GovernedLoopEffectAttempt>(saved.Attempt);
    }
}
