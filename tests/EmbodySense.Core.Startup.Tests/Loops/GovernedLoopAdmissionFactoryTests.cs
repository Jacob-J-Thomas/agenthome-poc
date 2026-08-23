using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Execution.Wait;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Startup.Capabilities;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class GovernedLoopAdmissionFactoryTests
{
    private const string ConversationTurnCapabilityId = "org.embodysense/conversation-turn";
    private const string ModelInferenceCapabilityId = "org.embodysense/model-inference";
    private const string ModelProfileCapabilityId = BuiltInCapabilityCatalog.CodexModelProfileCapabilityId;

    [Fact]
    public async Task Production_composition_preserves_system_role_mapping_without_creating_an_ambient_grant()
    {
        using var workspace = new TestWorkspace();
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        var catalog = await new LoopAuthoringFacade(workspace.RootPath).GetCatalogAsync();

        using var facade = GovernedLoopAdmissionFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            new FixedTimeProvider(AdmissionFixture.Now));
        var invalid = await facade.AdmitAsync(null);

        Assert.Equal("default-assistant", catalog.RoleId);
        Assert.Equal(new ContextualRoleRevisionIdentity("default-assistant", 1), catalog.SystemDefault.OwningRole.Identity);
        Assert.Equal(GovernedLoopAdmissionStatus.Invalid, invalid.Status);
        Assert.False(File.Exists(paths.AuthorityProfilesDocumentPath));
        Assert.False(File.Exists(paths.AuthorityProfilesProofPath));
    }

    [Fact]
    public async Task Caller_owned_composition_uses_the_supplied_model_routing_admission_service()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var fixture = AdmissionFixture.Create(workspaceId);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var ports = new MutableAdmissionPorts(fixture);
        var store = new GovernedLoopAdmissionStore(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            authorityTransaction: transaction);

        using var facade = GovernedLoopAdmissionFactory.Create(
            workspaceId,
            store,
            ports,
            ports,
            ports,
            ports,
            ports,
            ports,
            transaction,
            ports,
            new FixedTimeProvider(AdmissionFixture.Now));

        var admitted = await facade.AdmitAsync(fixture.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Admitted, admitted.Status);
        Assert.Equal(1, ports.ModelRoutingAdmissionCount);
    }

    [Fact]
    public async Task Seeded_catalog_marks_the_exact_first_wave_model_inference_capability_unavailable_when_its_output_ceiling_cannot_be_enforced()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trustProvider = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var fixture = AdmissionFixture.CreateFirstWave(workspaceId);
        Assert.Equal(
            GovernedLoopSequentialPlanBuildStatus.Ready,
            GovernedLoopSequentialPlanBuilder.Build(fixture.GraphRead.Artifact).Status);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var ports = new MutableAdmissionPorts(fixture);
        var store = new GovernedLoopAdmissionStore(paths, trustProvider, authorityTransaction: transaction);
        var capabilityAdmission = CapabilityAdmissionFactory.Create(
            paths,
            trustProvider,
            transaction,
            new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1)));
        var codexPath = workspace.File("codex-profile-test");
        await File.WriteAllTextAsync(codexPath, "exact test runtime");
        var profileOptions = new LlmInferenceClientOptions
        {
            Surface = LlmInferenceSurface.OpenAiCodex,
            Model = "test-model",
            WorkingDirectory = workspace.RootPath,
            CodexExecutablePath = codexPath,
            CodexSandbox = "read-only"
        };
        var profileRegistry = new ConfiguredModelProfileRegistry(
            profileOptions,
            new CodexRuntimeStatus(
                CodexRuntimeCompatibility.Compatible,
                codexPath,
                codexPath,
                "codex-cli test-version",
                "test-model",
                "test",
                "Exact test compatibility evidence."));
        var routingAdmission = new GovernedModelRoutingAdmissionService(
            new CapabilityCatalogStore(paths, trustProvider, authorityTransaction: transaction),
            profileRegistry,
            profileRegistry,
            profileRegistry,
            new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1)));

        using var facade = GovernedLoopAdmissionFactory.Create(
            workspaceId,
            store,
            ports,
            ports,
            ports,
            ports,
            capabilityAdmission,
            routingAdmission,
            transaction,
            ports,
            new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1)));
        var unavailable = await facade.AdmitAsync(fixture.Request);

        Assert.Equal(GovernedLoopAdmissionStatus.Unavailable, unavailable.Status);
        Assert.Null(unavailable.Outcome);
    }

    [Fact]
    public async Task Admission_accepts_each_application_owned_wait_descriptor_after_public_plan_validation()
    {
        Assert.Equal(2, GovernedLoopWaitNodeCatalogContract.Descriptors.Count);

        foreach (var descriptor in GovernedLoopWaitNodeCatalogContract.Descriptors)
        {
            using var workspace = new TestWorkspace();
            var paths = new WorkspacePaths(workspace.RootPath);
            var trustProvider = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
            await WorkspaceInitializer.ForFileCapabilityTrustRoot(workspace.ServerStatePath).InitializeAsync(workspace.RootPath);
            var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
            var fixture = AdmissionFixture.CreateWait(workspaceId, descriptor);
            Assert.Equal(
                GovernedLoopSequentialPlanBuildStatus.Ready,
                GovernedLoopSequentialPlanBuilder.Build(fixture.GraphRead.Artifact).Status);
            var transaction = new CapabilityAuthorityTransaction(paths);
            var ports = new MutableAdmissionPorts(fixture);
            var store = new GovernedLoopAdmissionStore(paths, trustProvider, authorityTransaction: transaction);
            var capabilityAdmission = CapabilityAdmissionFactory.Create(
                paths,
                trustProvider,
                transaction,
                new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1)));
            using var facade = GovernedLoopAdmissionFactory.Create(
                workspaceId,
                store,
                ports,
                ports,
                ports,
                ports,
                capabilityAdmission,
                transaction,
                ports,
                new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1)));

            var admitted = await facade.AdmitAsync(fixture.Request);

            Assert.Equal(GovernedLoopAdmissionStatus.Admitted, admitted.Status);
            var outcome = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(admitted.Outcome);
            var receipt = Assert.IsType<GovernedLoopAdmissionReceipt>(outcome.Receipt);
            Assert.Equal(
                [ConversationTurnCapabilityId],
                receipt.Evidence.CapabilityAdmission.Pins.Select(item => item.DescriptorIdentity.Id.Value));
        }
    }

    [Fact]
    public async Task Concrete_store_restart_replays_exact_workspace_outcome_before_mutable_authority()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var fixture = AdmissionFixture.Create(workspaceId);
        var firstTransaction = new CapabilityAuthorityTransaction(paths);
        var firstPorts = new MutableAdmissionPorts(fixture);
        var firstStore = new GovernedLoopAdmissionStore(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            authorityTransaction: firstTransaction);

        GovernedLoopAdmissionTerminalOutcome committed;
        using (var facade = GovernedLoopAdmissionFactory.Create(
                   workspaceId,
                   firstStore,
                   firstPorts,
                   firstPorts,
                   firstPorts,
                   firstPorts,
                   firstPorts,
                   firstTransaction,
                   firstPorts,
                   new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1))))
        {
            var admitted = await facade.AdmitAsync(fixture.Request);
            Assert.Equal(GovernedLoopAdmissionStatus.Admitted, admitted.Status);
            committed = Assert.IsType<GovernedLoopAdmissionTerminalOutcome>(admitted.Outcome);
        }

        Assert.Equal(workspaceId, committed.Intent.WorkspaceId);
        Assert.Equal(1, firstPorts.RunIdentityGenerationCount);
        var foreign = await firstStore.ReadByOperationAsync(
            "workspace-sha256:" + new string('f', ContextualRoleLimits.Sha256HexCharacters),
            fixture.Request.OperationId);
        Assert.Equal(GovernedLoopAdmissionStoreReadStatus.Unavailable, foreign.Status);

        var restartedTransaction = new CapabilityAuthorityTransaction(paths);
        var restartedStore = new GovernedLoopAdmissionStore(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            authorityTransaction: restartedTransaction);
        var restartedPorts = new MutableAdmissionPorts(fixture) { ThrowOnMutableAccess = true };
        using var restartedFacade = GovernedLoopAdmissionFactory.Create(
            workspaceId,
            restartedStore,
            restartedPorts,
            restartedPorts,
            restartedPorts,
            restartedPorts,
            restartedPorts,
            restartedTransaction,
            restartedPorts,
            new FixedTimeProvider(AdmissionFixture.Now.AddHours(1)));

        var replayed = await restartedFacade.AdmitAsync(fixture.Request);
        var changed = GovernedLoopAdmissionRequestHash.Apply(fixture.Request with { Surface = "cli" });
        var conflict = await restartedFacade.AdmitAsync(changed);

        Assert.Equal(GovernedLoopAdmissionStatus.Replayed, replayed.Status);
        Assert.Equal(committed.ContentHash, replayed.Outcome?.ContentHash);
        Assert.Equal(GovernedLoopAdmissionStatus.Conflict, conflict.Status);
        Assert.Equal(0, restartedPorts.MutableReadCount);
        Assert.Equal(0, restartedPorts.RunIdentityGenerationCount);
    }

    [Fact]
    public async Task Admission_holds_the_same_physical_workspace_fence_across_resolution_and_commit()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var fixture = AdmissionFixture.Create(workspaceId);
        var admissionTransaction = new CapabilityAuthorityTransaction(paths);
        var ports = new MutableAdmissionPorts(fixture) { PauseRoleResolution = true };
        var store = new GovernedLoopAdmissionStore(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            authorityTransaction: admissionTransaction);
        using var facade = GovernedLoopAdmissionFactory.Create(
            workspaceId,
            store,
            ports,
            ports,
            ports,
            ports,
            ports,
            admissionTransaction,
            ports,
            new FixedTimeProvider(AdmissionFixture.Now.AddMinutes(1)));

        var admissionTask = facade.AdmitAsync(fixture.Request);
        await ports.RoleResolutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var competitorAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var competitorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var competingTransaction = new CapabilityAuthorityTransaction(paths);
        var competitorTask = Task.Run(async () =>
        {
            competitorAttempted.SetResult();
            return await competingTransaction.ExecuteAsync(
                _ =>
                {
                    competitorEntered.SetResult();
                    return Task.FromResult(true);
                });
        });
        await competitorAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(competitorEntered.Task.IsCompleted);
        ports.ReleaseRoleResolution.SetResult();
        Assert.Equal(GovernedLoopAdmissionStatus.Admitted, (await admissionTask).Status);
        Assert.True(await competitorTask.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(competitorEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Disposed_facade_rejects_use_without_disposing_caller_owned_ports()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var workspaceId = CapabilityWorkspaceScopeId.Create(paths.RootPath);
        var fixture = AdmissionFixture.Create(workspaceId);
        var transaction = new CapabilityAuthorityTransaction(paths);
        var ports = new MutableAdmissionPorts(fixture);
        var store = new GovernedLoopAdmissionStore(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            authorityTransaction: transaction);
        var facade = GovernedLoopAdmissionFactory.Create(
            workspaceId,
            store,
            ports,
            ports,
            ports,
            ports,
            ports,
            transaction,
            ports,
            new FixedTimeProvider(AdmissionFixture.Now));

        facade.Dispose();
        facade.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => facade.AdmitAsync(fixture.Request));
        Assert.False(ports.IsDisposed);
    }

    private sealed class MutableAdmissionPorts :
        IGovernedLoopGraphRevisionStore,
        IGovernedLoopGrantBindingSource,
        IAuthorityGrantRoleSource,
        IAuthorityGrantResolver,
        ICapabilityAdmissionService,
        IGovernedModelRoutingAdmissionService,
        IGovernedLoopAdmissionRunIdentityGenerator,
        IDisposable
    {
        private readonly AdmissionFixture _fixture;

        internal MutableAdmissionPorts(AdmissionFixture fixture) => _fixture = fixture;

        internal bool ThrowOnMutableAccess { get; init; }

        internal bool PauseRoleResolution { get; init; }

        internal int MutableReadCount { get; private set; }

        internal int RunIdentityGenerationCount { get; private set; }

        internal int ModelRoutingAdmissionCount { get; private set; }

        internal bool IsDisposed { get; private set; }

        internal TaskCompletionSource RoleResolutionEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ReleaseRoleResolution { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(
            GovernedLoopRevisionReference revision,
            CancellationToken cancellationToken = default)
        {
            MutableRead(cancellationToken);
            return Task.FromResult(_fixture.GraphRead);
        }

        public Task<GovernedLoopGrantBindingResolution> ResolveAsync(
            GovernedLoopRevisionPublicationPin? pin,
            CancellationToken cancellationToken = default)
        {
            MutableRead(cancellationToken);
            return Task.FromResult(_fixture.BindingResolution);
        }

        public async Task<AuthorityGrantRoleResolution> ResolveAsync(
            ContextualRoleRevisionPin? pin,
            CancellationToken cancellationToken = default)
        {
            MutableRead(cancellationToken);
            if (PauseRoleResolution)
            {
                RoleResolutionEntered.TrySetResult();
                await ReleaseRoleResolution.Task.WaitAsync(cancellationToken);
            }

            return _fixture.RoleResolution;
        }

        public Task<AuthorityGrantResolution> ResolveAsync(
            AuthorityGrantReference? reference,
            CancellationToken cancellationToken = default)
        {
            MutableRead(cancellationToken);
            return Task.FromResult(_fixture.GrantResolution);
        }

        public Task<CapabilityAdmissionResult> AdmitAsync(
            CapabilityDependencyManifest requirements,
            IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
            CancellationToken cancellationToken = default)
        {
            MutableRead(cancellationToken);
            throw new InvalidOperationException("An empty authority ceiling must not consult capability catalog state.");
        }

        public Task<CapabilityRevalidationResult> RevalidateAsync(
            CapabilityAdmissionSnapshot snapshot,
            IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GovernedModelRoutingAdmissionResult> AdmitAsync(
            GovernedModelRoutingAdmissionRequest? request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModelRoutingAdmissionCount++;
            var exactRequest = Assert.IsType<GovernedModelRoutingAdmissionRequest>(request);
            var seed = exactRequest.Seed;
            Assert.Empty(exactRequest.Nodes);
            var snapshot = GovernedLoopAdmissionContractHash.CreateEmptyModelRoutingAdmission(
                seed.Intent,
                seed.Binding,
                seed.GrantProfile,
                seed.GrantBoundary,
                seed.GrantDependencyEvidenceHash,
                seed.EffectiveAuthority,
                seed.CapabilityAdmission,
                seed.EvaluatedAtUtc);
            return Task.FromResult(new GovernedModelRoutingAdmissionResult(GovernedModelRoutingAdmissionStatus.Admitted, snapshot));
        }

        public string CreateRunId()
        {
            RunIdentityGenerationCount++;
            if (ThrowOnMutableAccess)
            {
                throw new InvalidOperationException("Historical replay must not generate a replacement run identity.");
            }

            return "run-startup-admission-1";
        }

        public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(
            string graphId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(
            string graphId,
            string operationId,
            string lifecycleRequestHash,
            string authoringRequestHash,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(
            GovernedLoopGraphRevisionStoreMutation mutation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public void Dispose() => IsDisposed = true;

        private void MutableRead(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MutableReadCount++;
            if (ThrowOnMutableAccess)
            {
                throw new InvalidOperationException("Historical replay must precede mutable authority reads.");
            }
        }
    }

    private sealed record AdmissionFixture(
        GovernedLoopAdmissionRequest Request,
        GovernedLoopGraphRevisionArtifactReadResult GraphRead,
        GovernedLoopGrantBindingResolution BindingResolution,
        AuthorityGrantRoleResolution RoleResolution,
        AuthorityGrantResolution GrantResolution)
    {
        internal static readonly DateTimeOffset Now = new(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);

        internal static AdmissionFixture Create(string workspaceId) => Create(workspaceId, includeModelInference: false);

        internal static AdmissionFixture CreateFirstWave(string workspaceId) => Create(workspaceId, includeModelInference: true);

        internal static AdmissionFixture CreateWait(string workspaceId, GovernedLoopNodeCatalogDescriptor descriptor)
            => Create(workspaceId, includeModelInference: false, descriptor);

        private static AdmissionFixture Create(
            string workspaceId,
            bool includeModelInference,
            GovernedLoopNodeCatalogDescriptor? waitDescriptor = null)
        {
            var includeConversationTurn = includeModelInference || waitDescriptor is not null;
            var capabilityIdentities = includeModelInference
                ? new[] { CapabilityIdentity(ConversationTurnCapabilityId), CapabilityIdentity(ModelInferenceCapabilityId), CapabilityIdentity(ModelProfileCapabilityId) }
                : includeConversationTurn
                    ? new[] { CapabilityIdentity(ConversationTurnCapabilityId) }
                    : [];
            var role = CreateRole(workspaceId, includeConversationTurn, includeModelInference);
            var rolePin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
            var graph = waitDescriptor is null
                ? CreateGraph(rolePin, includeModelInference)
                : CreateWaitGraph(rolePin, waitDescriptor);
            var revisionArtifact = GovernedLoopRevisionArtifactFactory.Create(
                1,
                graph.RevisionReference,
                null,
                null,
                "create-loop",
                "user-owner",
                Now.AddHours(-1));
            var artifact = GovernedLoopGraphRevisionArtifactFactory.Create(1, revisionArtifact, graph);
            var publication = GovernedLoopRevisionPublicationPinFactory.Create(
                1,
                graph.RevisionReference,
                "publish-loop",
                Hash64('7'));
            var ceiling = capabilityIdentities.Length == 0
                ? AuthorityCeilingIntersection.EmptyCeiling()
                : new AuthorityCeiling(
                    capabilityIdentities,
                    [],
                    0,
                    CapabilitySideEffectClass.None,
                    false,
                    false,
                    false);
            var profile = CreateProfile(ceiling);
            var binding = new AuthorityGrantBinding(
                new AuthorityGrantProfilePin(
                    new AuthorityProfileReference(profile.ProfileId, profile.Revision),
                    ProfileHash(profile)),
                rolePin,
                publication);
            var grant = CreateGrant(binding, ceiling);
            var grantReference = new AuthorityGrantReference(grant.GrantId, grant.Revision, grant.ContentHash);
            var request = GovernedLoopAdmissionRequestHash.Apply(new GovernedLoopAdmissionRequest(
                GovernedLoopAdmissionRequest.CurrentSchemaVersion,
                "admit-startup-loop-1",
                Hash64('1'),
                string.Empty,
                publication,
                grantReference,
                Actor(),
                "web"));
            var lifecycle = new ContextualRoleLifecycleSnapshot(
                1,
                role.Identity.RoleId,
                role.Identity,
                ContextualRoleLifecycleState.Active,
                "publish-role",
                ContextualRoleRevisionMutationKind.Create,
                Now.AddMinutes(-10));

            return new AdmissionFixture(
                request,
                new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ready, 1, artifact),
                new GovernedLoopGrantBindingResolution(
                    AuthorityGrantDependencyStatus.Active,
                    publication,
                    artifact,
                    rolePin,
                    artifact.Graph.AuthorityCeiling.CapabilityIds,
                    Hash64('2')),
                new AuthorityGrantRoleResolution(
                    AuthorityGrantDependencyStatus.Active,
                    rolePin,
                    role,
                    lifecycle,
                    workspaceId,
                    ContextualRoleInstructionSourceProbeStatus.Ready,
                    Hash64('3')),
                new AuthorityGrantResolution(
                    AuthorityGrantResolutionStatus.Active,
                    grantReference,
                    grant,
                    ceiling,
                    Hash64('4'),
                    Now));
        }

        private static ContextualRoleRevision CreateRole(
            string workspaceId,
            bool includeConversationTurn,
            bool includeModelInference)
        {
            var role = new ContextualRoleRevision(
                1,
                new ContextualRoleRevisionIdentity("bounded-helper", 1),
                string.Empty,
                "Bounded helper",
                "Performs bounded governed-loop work.",
                ContextualRoleStatus.Published,
                new ContextualRoleProvenance("user-owner", Now.AddHours(-2), Now.AddHours(-1)),
                new ContextualRoleWorkspaceApplicability([workspaceId]),
                new ContextualRoleInstructionSourceReference(
                    ContextualRoleInstructionSourceKind.RoleArtifact,
                    "bounded-helper-source",
                    ContextualRoleInstructionClassification.RoleInstruction),
                new ContextualRolePolicyMaxima(
                    includeModelInference
                        ? ImmutableArray.Create(ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId)
                        : includeConversationTurn
                            ? ImmutableArray.Create(ConversationTurnCapabilityId)
                            : ImmutableArray<string>.Empty));
            return ContextualRoleRevisionContentHash.Apply(role);
        }

        private static GovernedLoopGraphDefinition CreateGraph(ContextualRoleRevisionPin owningRole, bool includeModelInference)
        {
            if (includeModelInference)
            {
                return CreateFirstWaveGraph(owningRole);
            }

            var candidate = new GovernedLoopGraphCandidate(
                1,
                "governed-loop",
                "revision-1",
                "Execute one bounded governed operation.",
                owningRole,
                "trigger",
                ["exit"],
                GovernedLoopAuthorityCeiling.Create([]),
                [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
                [
                    new GovernedLoopNodeDefinition(
                        "trigger",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                        [new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true)],
                        GovernedLoopAuthorityCeiling.Create([]),
                        new Dictionary<string, string>()),
                    new GovernedLoopNodeDefinition(
                        "exit",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                        [
                            new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                            new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                        ],
                        GovernedLoopAuthorityCeiling.Create([]),
                        new Dictionary<string, string>()),
                ],
                [new GovernedLoopControlEdgeDefinition("trigger-to-exit", "trigger", "exit", GovernedLoopControlCondition.Always)],
                [new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "request")],
                new GovernedLoopOutputContract(
                    "Return the bounded result.",
                    [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
                new GovernedLoopDisplayMetadata(
                    "Governed loop",
                    "Test-only governed loop.",
                    [
                        new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                        new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 100, 0),
                    ]),
                EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
            return Assert.IsType<GovernedLoopGraphDefinition>(GovernedLoopGraphNormalizer.Normalize(candidate).Graph);
        }

        private static GovernedModelRoutingPolicy RuntimeRoutingPolicy()
        {
            Assert.True(CapabilityId.TryParse(ModelProfileCapabilityId, out var profileId, out _));
            Assert.True(CapabilityDataClass.TryParse("sensitive", out var sensitiveData, out _));
            var unbounded = GovernedModelUsageCeiling.Create(
                GovernedModelUsageLimit.Unbounded,
                GovernedModelUsageLimit.Unbounded,
                GovernedModelUsageLimit.Unbounded,
                GovernedModelUsageLimit.Unbounded,
                GovernedModelMonetaryLimit.Unbounded);
            var privacy = GovernedModelPrivacyRequirement.Create(
                1,
                localOnly: false,
                CapabilityEgressMode.Unrestricted,
                [],
                [sensitiveData!],
                [],
                GovernedModelRetentionPosture.Indefinite,
                GovernedModelTrainingPosture.Allowed);
            return GovernedModelRoutingPolicy.Create(
                1,
                GovernedModelRoutingSelector.Exact(profileId!),
                [],
                GovernedModelProfileRequirements.Create(
                    1,
                    [GovernedModelModality.Text],
                    [],
                    1,
                    1,
                    privacy,
                    GovernedModelBudgetPolicy.Create(1, unbounded, unbounded, unbounded)));
        }

        private static GovernedLoopGraphDefinition CreateFirstWaveGraph(ContextualRoleRevisionPin owningRole)
        {
            var candidate = new GovernedLoopGraphCandidate(
                1,
                "governed-first-wave-loop",
                "revision-1",
                "Execute one bounded model-inference operation.",
                owningRole,
                "trigger",
                ["exit"],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId, ModelInferenceCapabilityId, ModelProfileCapabilityId]),
                [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
                [
                    new GovernedLoopNodeDefinition(
                        "trigger",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                        [
                            new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                            new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, "text", true),
                        ],
                        GovernedLoopAuthorityCeiling.Create([]),
                        new Dictionary<string, string>()),
                    new GovernedLoopNodeDefinition(
                        "inference",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Inference, "provider-inference", 1),
                        [
                            new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                            new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context, "text", true),
                            new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                        ],
                        GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId, ModelProfileCapabilityId]),
                        new Dictionary<string, string> { ["instruction"] = "Answer the admitted request." }),
                    new GovernedLoopNodeDefinition(
                        "exit",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                        [
                            new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                            new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                        ],
                        GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                        new Dictionary<string, string>()),
                ],
                [
                    new GovernedLoopControlEdgeDefinition("trigger-to-inference", "trigger", "inference", GovernedLoopControlCondition.Always),
                    new GovernedLoopControlEdgeDefinition("inference-to-exit", "inference", "exit", GovernedLoopControlCondition.Success),
                ],
                [
                    new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "inference", "request"),
                    new GovernedLoopBindingDefinition("context-binding", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "inference", "invocation-context"),
                    new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, "inference", "result", "exit", "result"),
                ],
                new GovernedLoopOutputContract(
                    "Return the bounded result.",
                    [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
                new GovernedLoopDisplayMetadata(
                    "Governed first-wave loop",
                    "Test-only first-wave governed loop.",
                    [
                        new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                        new GovernedLoopNodeDisplayMetadata("inference", "Inference", "Infer.", 100, 0),
                        new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 200, 0),
                    ]),
                RuntimeRoutingPolicy());
            return Assert.IsType<GovernedLoopGraphDefinition>(GovernedLoopGraphNormalizer.Normalize(candidate).Graph);
        }

        private static GovernedLoopGraphDefinition CreateWaitGraph(
            ContextualRoleRevisionPin owningRole,
            GovernedLoopNodeCatalogDescriptor descriptor)
        {
            var parameter = Assert.Single(descriptor.Parameters);
            var parameterValue = descriptor.Descriptor.TypeId == GovernedLoopWaitVocabulary.Timestamp
                ? Now.AddMinutes(5).ToString(GovernedLoopWaitVocabulary.CanonicalUtcTimestampFormat, System.Globalization.CultureInfo.InvariantCulture)
                : "authenticated-event-1";
            var candidate = new GovernedLoopGraphCandidate(
                1,
                $"governed-{descriptor.Descriptor.TypeId}-loop",
                "revision-1",
                "Wait durably before completing one admitted operation.",
                owningRole,
                "trigger",
                ["exit"],
                GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
                [
                    new GovernedLoopNodeDefinition(
                        "trigger",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Trigger, "manual-trigger", 1),
                        [
                            new GovernedLoopPortDefinition("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                            new GovernedLoopPortDefinition("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context, "text", true),
                        ],
                        GovernedLoopAuthorityCeiling.Create([]),
                        new Dictionary<string, string>()),
                    new GovernedLoopNodeDefinition(
                        "wait",
                        descriptor.Descriptor,
                        [],
                        GovernedLoopAuthorityCeiling.Create([]),
                        new Dictionary<string, string>(StringComparer.Ordinal) { [parameter.Id] = parameterValue }),
                    new GovernedLoopNodeDefinition(
                        "exit",
                        new GovernedLoopNodeDescriptor(GovernedLoopNodeKind.Exit, "success-exit", 1),
                        [
                            new GovernedLoopPortDefinition("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data, "text", true),
                            new GovernedLoopPortDefinition("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data, "text", true),
                        ],
                        GovernedLoopAuthorityCeiling.Create([ConversationTurnCapabilityId]),
                        new Dictionary<string, string>()),
                ],
                [
                    new GovernedLoopControlEdgeDefinition("trigger-to-wait", "trigger", "wait", GovernedLoopControlCondition.Always),
                    new GovernedLoopControlEdgeDefinition("wait-to-exit", "wait", "exit", GovernedLoopControlCondition.Success),
                ],
                [new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "exit", "result")],
                new GovernedLoopOutputContract(
                    "Return the admitted result.",
                    [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
                new GovernedLoopDisplayMetadata(
                    "Governed Wait loop",
                    "Test-only governed Wait loop.",
                    [
                        new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                        new GovernedLoopNodeDisplayMetadata("wait", "Wait", "Sleep.", 100, 0),
                        new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 200, 0),
                    ]),
                EmbodySense.Core.Application.Tests.GovernedModelProfileApplicationTestFixture.DefaultRoutingPolicy());
            return Assert.IsType<GovernedLoopGraphDefinition>(GovernedLoopGraphNormalizer.Normalize(candidate).Graph);
        }

        private static CapabilityDescriptorIdentity CapabilityIdentity(string capabilityId)
        {
            var descriptor = Assert.Single(
                BuiltInCapabilityCatalog.Descriptors,
                item => item.Id.Value == capabilityId);
            Assert.True(CapabilityDescriptorHash.TryCompute(descriptor, out var hash, out var validation));
            Assert.True(validation.IsValid);
            return new CapabilityDescriptorIdentity(descriptor.Id, descriptor.Version, hash!);
        }

        private static AuthorityProfile CreateProfile(AuthorityCeiling ceiling)
            => new(
                1,
                ProfileId(),
                ProfileRevision(1),
                AuthorityProfileStatus.Active,
                Purpose(),
                new AuthorityProvenance(Actor(), AuthorityProvenanceKind.UserDeclaration),
                Now.AddHours(-1),
                null,
                ceiling,
                []);

        private static AuthorityProfileHash ProfileHash(AuthorityProfile profile)
        {
            Assert.True(AuthorityProfileHash.TryCompute(profile, out var hash, out var validation));
            Assert.True(validation.IsValid);
            return hash!;
        }

        private static AuthorityGrant CreateGrant(AuthorityGrantBinding binding, AuthorityCeiling ceiling)
        {
            var grant = new AuthorityGrant(
                1,
                GrantId(),
                GrantRevision(1),
                null,
                null,
                AuthorityGrantLifecycleStatus.Active,
                binding,
                ceiling,
                new AuthorityGrantBoundary(Now.AddMinutes(-1), Now.AddHours(1), AuthorityGrantCompletionConstraintKind.None),
                Actor(),
                Purpose(),
                Now,
                string.Empty);
            return AuthorityGrantHash.Apply(grant);
        }

        private static AuthorityActorId Actor()
        {
            Assert.True(AuthorityActorId.TryParse("user-owner", out var result, out _));
            return result!;
        }

        private static AuthorityPurpose Purpose()
        {
            Assert.True(AuthorityPurpose.TryParse("Delegate bounded work for one governed loop revision.", out var result, out _));
            return result!;
        }

        private static AuthorityProfileId ProfileId()
        {
            Assert.True(AuthorityProfileId.TryParse("default-profile", out var result, out _));
            return result!;
        }

        private static AuthorityProfileRevision ProfileRevision(int value)
        {
            Assert.True(AuthorityProfileRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var result, out _));
            return result!;
        }

        private static AuthorityGrantId GrantId()
        {
            Assert.True(AuthorityGrantId.TryParse("workspace-helper", out var result, out _));
            return result!;
        }

        private static AuthorityGrantRevision GrantRevision(int value)
        {
            Assert.True(AuthorityGrantRevision.TryParse(value.ToString(System.Globalization.CultureInfo.InvariantCulture), out var result, out _));
            return result!;
        }

        private static string Hash64(char value) => new(value, 64);
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
