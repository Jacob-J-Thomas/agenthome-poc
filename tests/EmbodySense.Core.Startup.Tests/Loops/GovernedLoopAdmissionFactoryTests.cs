using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Loops.Admission;
using EmbodySense.Core.Application.Loops.Admission.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Admission;
using EmbodySense.Core.Common.Loops.Admission.Models;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Loops.Admission;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class GovernedLoopAdmissionFactoryTests
{
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
        IGovernedLoopAdmissionRunIdentityGenerator,
        IDisposable
    {
        private readonly AdmissionFixture _fixture;

        internal MutableAdmissionPorts(AdmissionFixture fixture) => _fixture = fixture;

        internal bool ThrowOnMutableAccess { get; init; }

        internal bool PauseRoleResolution { get; init; }

        internal int MutableReadCount { get; private set; }

        internal int RunIdentityGenerationCount { get; private set; }

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

        internal static AdmissionFixture Create(string workspaceId)
        {
            var role = CreateRole(workspaceId);
            var rolePin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
            var graph = CreateGraph(rolePin);
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
            var ceiling = AuthorityCeilingIntersection.EmptyCeiling();
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

        private static ContextualRoleRevision CreateRole(string workspaceId)
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
                new ContextualRolePolicyMaxima(ImmutableArray<string>.Empty));
            return ContextualRoleRevisionContentHash.Apply(role);
        }

        private static GovernedLoopGraphDefinition CreateGraph(ContextualRoleRevisionPin owningRole)
        {
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
                    ]));
            return Assert.IsType<GovernedLoopGraphDefinition>(GovernedLoopGraphNormalizer.Normalize(candidate).Graph);
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
