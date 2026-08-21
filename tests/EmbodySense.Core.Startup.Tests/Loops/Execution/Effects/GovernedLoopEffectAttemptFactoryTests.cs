using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Loops.EffectAuthorityEvidence.Models;
using EmbodySense.Core.Application.Loops.Execution.Authority;
using EmbodySense.Core.Application.Loops.Execution.Authority.Models;
using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;
using EmbodySense.Core.Application.Tests.Loops.Execution.Effects;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Loops.Execution.Authority;
using EmbodySense.Core.Common.Loops.Execution.Authority.Models;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Loops.Execution.Effects;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

public sealed class GovernedLoopEffectAttemptFactoryTests
{
    [Fact]
    public async Task Inert_factory_prevents_dispatch_when_current_catalog_truth_is_unavailable()
    {
        using var workspace = new TestWorkspace();
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var catalog = new ProbeCatalogStore(fixture) { Unavailable = true };
        var operation = new ProbeOperation(fixture.Descriptor);
        var authority = new DirectAuthorityBoundary();
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var hostVersion, out _));
        Assert.True(CapabilityPlatform.TryParse("linux/x64", out var hostPlatform, out _));
        var facade = GovernedLoopEffectAttemptFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            catalog,
            new GovernedActuatorOperationRegistry([operation]),
            authority,
            hostVersion!,
            hostPlatform!,
            new FixedTimeProvider(GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1)));

        var result = await facade.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.CatalogUnavailable, result.Status);
        Assert.Null(result.Attempt);
        Assert.Equal(1, catalog.ReadCalls);
        Assert.Equal(0, operation.PrepareCalls);
        Assert.Equal(0, operation.ExecuteCalls);
        Assert.Equal(0, authority.Calls);
    }

    [Fact]
    public async Task Inert_factory_executes_one_probe_and_replays_committed_evidence_after_restart()
    {
        using var workspace = new TestWorkspace();
        var fixture = GovernedLoopEffectAttemptTestFixture.Create();
        var catalog = new ProbeCatalogStore(fixture);
        var operation = new ProbeOperation(fixture.Descriptor);
        var authority = new DirectAuthorityBoundary();
        Assert.True(CapabilityVersion.TryParse("1.0.0", out var hostVersion, out _));
        Assert.True(CapabilityPlatform.TryParse("linux/x64", out var hostPlatform, out _));
        var facade = GovernedLoopEffectAttemptFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            catalog,
            new GovernedActuatorOperationRegistry([operation]),
            authority,
            hostVersion!,
            hostPlatform!,
            new FixedTimeProvider(GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1)));

        var committed = await facade.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopEffectPhase.Committed, committed.Attempt?.Payload.Phase);
        Assert.Equal(2, catalog.ReadCalls);
        Assert.Equal(1, operation.PrepareCalls);
        Assert.Equal(1, operation.ExecuteCalls);
        Assert.Equal(1, authority.Calls);

        var replayCatalog = new ProbeCatalogStore(fixture) { Unavailable = true };
        var replayOperation = new ProbeOperation(fixture.Descriptor);
        var replayFacade = GovernedLoopEffectAttemptFactory.Create(
            new WorkspacePaths(workspace.RootPath),
            replayCatalog,
            new GovernedActuatorOperationRegistry([replayOperation]),
            new UnusedAuthorityBoundary(),
            hostVersion!,
            hostPlatform!,
            new FixedTimeProvider(GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(2)));

        var replayed = await replayFacade.ExecuteAsync(fixture.Request);

        Assert.Equal(GovernedLoopEffectAttemptExecutionStatus.Replayed, replayed.Status);
        Assert.Equal(committed.Attempt, replayed.Attempt);
        Assert.Equal(0, replayCatalog.ReadCalls);
        Assert.Equal(0, replayOperation.PrepareCalls);
        Assert.Equal(0, replayOperation.ExecuteCalls);
    }

    [Fact]
    public async Task Production_factory_projects_the_initialized_catalog_through_the_surface_neutral_facade()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var facade = GovernedLoopEffectAttemptFactory.Create(
            paths,
            trust,
            transaction,
            new GovernedActuatorOperationRegistry([]),
            new UnusedAuthorityBoundary(transaction));

        var catalog = await facade.ReadCatalogAsync(8);

        Assert.Equal(GovernedActuatorCatalogReadStatus.Available, catalog.Status);
        Assert.Empty(catalog.Operations);
    }

    [Fact]
    public void Production_factory_rejects_an_authority_boundary_for_another_workspace_transaction()
    {
        using var workspace = new TestWorkspace();
        using var otherWorkspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var unrelatedTransaction = new CapabilityAuthorityTransaction(new WorkspacePaths(otherWorkspace.RootPath));

        var exception = Assert.Throws<ArgumentException>(() => GovernedLoopEffectAttemptFactory.Create(
            paths,
            trust,
            transaction,
            new GovernedActuatorOperationRegistry([]),
            new UnusedAuthorityBoundary(unrelatedTransaction)));

        Assert.Equal("authorityBoundary", exception.ParamName);
        Assert.Contains("same workspace authority transaction", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_rejects_missing_server_owned_dependencies()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var registry = new GovernedActuatorOperationRegistry([]);
        var authority = new UnusedAuthorityBoundary(transaction);

        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAttemptFactory.Create(null!, trust, transaction, registry, authority));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAttemptFactory.Create(paths, null!, transaction, registry, authority));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAttemptFactory.Create(paths, trust, null!, registry, authority));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAttemptFactory.Create(paths, trust, transaction, null!, authority));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopEffectAttemptFactory.Create(paths, trust, transaction, registry, null!));
    }

    private sealed class UnusedAuthorityBoundary(ICapabilityAuthorityTransaction? authorityTransaction = null) : IGovernedLoopEffectAuthorityDecisionBoundary
    {
        public ICapabilityAuthorityTransaction AuthorityTransaction => authorityTransaction
            ?? throw new InvalidOperationException("The inert test boundary has no production workspace authority transaction.");

        public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Catalog projection must not evaluate effect authority.");

        public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Catalog projection must not evaluate effect authority.");
    }

    private sealed class ProbeCatalogStore : ICapabilityCatalogStore
    {
        private readonly CapabilityCatalogEntry _entry;

        internal ProbeCatalogStore(GovernedLoopEffectAttemptTestFixture fixture)
        {
            var lifecycle = new CapabilityLifecycleSnapshot(
                CapabilityLifecycleSnapshot.CurrentSchemaVersion,
                fixture.Descriptor.Capability,
                CapabilityDeclarationState.Declared,
                CapabilityInstallationState.Installed,
                CapabilityEnablementState.Enabled,
                CapabilityHealthState.Healthy,
                CapabilityRetirementState.Active,
                CapabilityTrustState.Verified);
            _entry = new CapabilityCatalogEntry(
                fixture.Capability,
                lifecycle,
                1,
                GovernedLoopEffectAttemptTestFixture.Now,
                "activate-probe");
        }

        internal bool Unavailable { get; init; }
        internal int ReadCalls { get; private set; }

        public Task<CapabilityCatalogReadResult> ReadAsync(
            string? startAfterId,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            return Task.FromResult(Unavailable
                ? new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Unavailable, null, "unavailable")
                : new CapabilityCatalogReadResult(CapabilityCatalogReadStatus.Available, new CapabilityCatalogPage(1, [_entry], null), "available"));
        }

        public Task<CapabilityCatalogMutationResult> MutateAsync(
            CapabilityCatalogMutation mutation,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Effect execution must not mutate capability lifecycle truth.");
    }

    private sealed class ProbeOperation(GovernedActuatorOperationDescriptor descriptor) : IGovernedActuatorOperation
    {
        internal int PrepareCalls { get; private set; }
        internal int ExecuteCalls { get; private set; }
        public GovernedActuatorOperationDescriptor Descriptor { get; } = descriptor;
        public string? ValidateInput(GovernedActuatorInputEvidence input) => null;

        public Task<GovernedActuatorPreparationEvidence?> PrepareAsync(
            GovernedActuatorInputEvidence input,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PrepareCalls++;
            return Task.FromResult<GovernedActuatorPreparationEvidence?>(new(
                GovernedLoopEffectAttemptTestFixture.HashInput("target:alpha"),
                GovernedLoopEffectAttemptTestFixture.Hash('e'),
                "before-alpha"));
        }

        public async Task<GovernedActuatorAdapterResult> ExecuteAsync(
            GovernedActuatorInvocation invocation,
            IGovernedActuatorDispatchBoundary dispatchBoundary,
            CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            var outcome = await dispatchBoundary.CrossAsync(
                _ => Task.FromResult(new GovernedActuatorExternalOutcome(
                    GovernedLoopEffectOutcome.Succeeded,
                    "outcome-alpha",
                    "after-alpha")),
                cancellationToken);
            return new GovernedActuatorAdapterResult(GovernedActuatorAdapterStatus.OutcomeObserved, outcome);
        }
    }

    private sealed class DirectAuthorityBoundary : IGovernedLoopEffectAuthorityDecisionBoundary
    {
        internal int Calls { get; private set; }
        public ICapabilityAuthorityTransaction AuthorityTransaction => throw new InvalidOperationException("The inert test boundary has no production workspace authority transaction.");

        public Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
            => ExecuteWithDecisionAsync(request, (_, token) => commit(token), cancellationToken);

        public async Task<GovernedLoopEffectAuthorityExecutionResult<TResult>> ExecuteWithDecisionAsync<TResult>(
            GovernedLoopEffectAuthorityRequest request,
            Func<GovernedLoopEffectAuthorityDecision, CancellationToken, Task<TResult>> commit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var receipt = request.AdmissionReceipt;
            var proof = new GovernedLoopEffectAuthorityProof(
                GovernedLoopEffectAuthorityProof.CurrentSchemaVersion,
                receipt.Intent.AuthorityGrant,
                new AuthorityGrantBinding(receipt.Evidence.GrantProfile, receipt.Intent.Role, receipt.Intent.Publication),
                AuthorityGrantLifecycleStatus.Active,
                GovernedLoopEffectAuthorityGrantPosture.Active,
                receipt.Evidence.GrantBoundary,
                receipt.Evidence.EffectiveAuthority,
                receipt.Evidence.CapabilityAdmission.Pins,
                [],
                receipt.Evidence.GrantDependencyEvidenceHash);
            var decision = GovernedLoopEffectAuthorityContractHash.Apply(new GovernedLoopEffectAuthorityDecision(
                GovernedLoopEffectAuthorityDecision.CurrentSchemaVersion,
                request.ExecutionBinding.RunId,
                request.ExecutionBinding.ExecutionGeneration,
                request.NodeId,
                request.NodeAttempt,
                request.EffectOperationId,
                request.CorrelationId,
                request.BoundaryKind,
                receipt.ContentHash,
                proof,
                proof,
                request.RequiredAuthority,
                request.RequiredAuthority,
                request.RequiredCapabilityPins,
                GovernedLoopEffectAuthorityDisposition.Direct,
                GovernedLoopEffectAuthorityReason.ActiveExact,
                GovernedLoopEffectAttemptTestFixture.Now.AddMinutes(1),
                string.Empty));
            var result = await commit(decision, cancellationToken);
            return new GovernedLoopEffectAuthorityExecutionResult<TResult>(
                GovernedLoopEffectAuthorityExecutionStatus.Decided,
                decision,
                GovernedLoopEffectAuthorityEvidenceStoreStatus.Appended,
                true,
                result,
                "direct",
                decision.ContentHash);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utc;
    }
}
