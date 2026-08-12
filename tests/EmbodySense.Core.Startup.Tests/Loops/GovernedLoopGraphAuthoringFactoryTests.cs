using System.Collections.Immutable;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Loops;

public sealed class GovernedLoopGraphAuthoringFactoryTests
{
    private const string ModelInferenceCapabilityId = "org.embodysense/model/inference";
    private const string WorkspaceReadCapabilityId = "org.embodysense/workspace/read";
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string _workspaceId = "workspace-sha256:" + new string('a', ContextualRoleLimits.Sha256HexCharacters);

    [Fact]
    public async Task Concrete_factory_persists_create_and_exactly_replays_after_restart_without_starting_runtime()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var candidate = Candidate();
        var normalized = GovernedLoopGraphNormalizer.Normalize(candidate);
        Assert.True(normalized.IsValid);
        var reference = normalized.Graph!.RevisionReference;
        var request = new GovernedLoopGraphAuthoringRequest(
            1,
            new GovernedLoopRevisionLifecycleRequest(
                1,
                "create-research-loop",
                GovernedLoopRevisionOperationKind.CreateDraft,
                reference.GraphId,
                Actor(),
                GovernedLoopRevisionLifecycleStatus.Unknown,
                0,
                null,
                null,
                reference,
                null,
                null),
            candidate);
        var catalog = new RecordingNodeCatalog(Catalog(candidate));
        var authority = new RecordingAuthorityProvider(Authority());
        var authorizer = new RecordingActorAuthorizer();

        var created = await GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            catalog,
            authority,
            authorizer,
            new FixedTimeProvider(_now)).MutateAsync(request);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, created.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.Initial, created.ChangeKind);
        Assert.NotNull(created.RevisionIdentity);
        Assert.NotNull(created.LifecycleResult?.Evidence);
        Assert.Equal(1, catalog.Calls);
        Assert.Equal(1, authority.Calls);
        Assert.Equal(2, authorizer.Calls);

        var restartCatalog = new RecordingNodeCatalog(Catalog(candidate));
        var restartAuthority = new RecordingAuthorityProvider(Authority());
        var restartAuthorizer = new RecordingActorAuthorizer();
        var replayed = await GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath),
            restartCatalog,
            restartAuthority,
            restartAuthorizer,
            new FixedTimeProvider(_now.AddDays(1))).MutateAsync(request);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Replayed, replayed.Status);
        Assert.Equal(created.RevisionIdentity, replayed.RevisionIdentity);
        Assert.Equal(created.LifecycleResult.Evidence, replayed.LifecycleResult?.Evidence);
        Assert.Equal(0, restartCatalog.Calls);
        Assert.Equal(0, restartAuthority.Calls);
        Assert.Equal(0, restartAuthorizer.Calls);
        Assert.True(Directory.Exists(Path.Combine(paths.AgentPath, "loops", "revisions", "graph-authoring")));
        Assert.False(Directory.Exists(paths.LoopRunsPath));
        Assert.False(Directory.Exists(paths.CustomLoopDefinitionsPath));
    }

    [Fact]
    public async Task Factory_reuses_the_exact_supplied_authority_transaction()
    {
        var transaction = new RecordingAuthorityTransaction();

        var service = GovernedLoopGraphAuthoringFactory.Create(
            new UnusedRevisionStore(),
            new UnusedNodeCatalog(),
            new UnusedAuthorityProvider(),
            new UnusedActorAuthorizer(),
            transaction);
        var result = await service.MutateAsync(null);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Invalid, result.Status);
        Assert.Equal(1, transaction.ExecuteCount);
    }

    [Fact]
    public void Production_default_factory_composes_without_touching_workspace_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);

        var service = GovernedLoopGraphAuthoringFactory.Create(
            paths,
            new UnusedNodeCatalog(),
            new UnusedAuthorityProvider(),
            new UnusedActorAuthorizer());

        Assert.NotNull(service);
        Assert.False(Directory.Exists(paths.AgentPath));
    }

    [Fact]
    public void Factory_rejects_missing_server_owned_dependencies()
    {
        var store = new UnusedRevisionStore();
        var catalog = new UnusedNodeCatalog();
        var authority = new UnusedAuthorityProvider();
        var authorizer = new UnusedActorAuthorizer();
        var transaction = new RecordingAuthorityTransaction();

        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, catalog, authority, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, null!, authority, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, null!, authorizer, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, authority, null!, transaction));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(store, catalog, authority, authorizer, null!));
    }

    [Fact]
    public void Concrete_factory_rejects_missing_server_owned_dependencies()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var trust = new FileCapabilityCatalogTrustProvider(workspace.ServerStatePath);
        var catalog = new UnusedNodeCatalog();
        var authority = new UnusedAuthorityProvider();
        var authorizer = new UnusedActorAuthorizer();

        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(null!, trust, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, null!, catalog, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, null!, authority, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, catalog, null!, authorizer));
        Assert.Throws<ArgumentNullException>(() => GovernedLoopGraphAuthoringFactory.Create(paths, trust, catalog, authority, null!));
    }

    private static GovernedLoopGraphCandidate Candidate()
        => new(
            1,
            "research-loop",
            "revision-1",
            "Research one question safely.",
            RolePin(),
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId, WorkspaceReadCapabilityId]),
            [new GovernedLoopValueSchemaDefinition("text", GovernedLoopValueKind.Text, false)],
            Nodes(),
            [
                new GovernedLoopControlEdgeDefinition("trigger-to-infer", "trigger", "infer", GovernedLoopControlCondition.Always),
                new GovernedLoopControlEdgeDefinition("infer-to-exit", "infer", "exit", GovernedLoopControlCondition.Success),
            ],
            [
                new GovernedLoopBindingDefinition("request-binding", GovernedLoopBindingKind.Data, "trigger", "request", "infer", "request"),
                new GovernedLoopBindingDefinition("context-binding", GovernedLoopBindingKind.Context, "trigger", "invocation-context", "infer", "invocation-context"),
                new GovernedLoopBindingDefinition("result-binding", GovernedLoopBindingKind.Data, "infer", "result", "exit", "result"),
            ],
            new GovernedLoopOutputContract("Return the result.", [new GovernedLoopOutputDefinition("result", "text", "exit", "published-result", true)]),
            new GovernedLoopDisplayMetadata(
                "Research loop",
                "Display only.",
                [
                    new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata("infer", "Inference", "Answer.", 100, 0),
                    new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 200, 0),
                ]));

    private static GovernedLoopNodeDefinition[] Nodes()
        =>
        [
            new("trigger", new(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            new("infer", new(GovernedLoopNodeKind.Inference, "provider-inference", 1), [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([ModelInferenceCapabilityId]), new Dictionary<string, string> { ["instruction"] = "Answer safely." }),
            new("exit", new(GovernedLoopNodeKind.Exit, "success-exit", 1), [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
        ];

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static GovernedLoopNodeCatalogSnapshot Catalog(GovernedLoopGraphCandidate candidate)
    {
        var schemas = candidate.ValueSchemas!.Cast<GovernedLoopValueSchemaDefinition>().ToDictionary(schema => schema.Id, schema => schema.Kind, StringComparer.Ordinal);
        var terminals = candidate.TerminalNodeIds!.Cast<string>().ToHashSet(StringComparer.Ordinal);
        return new GovernedLoopNodeCatalogSnapshot(
            true,
            "catalog-1",
            candidate.Nodes!.Cast<GovernedLoopNodeDefinition>().Select(node =>
            {
                var outcomes = candidate.ControlEdges!
                    .Where(edge => edge!.FromNodeId == node.Id)
                    .Select(edge => edge!.Condition)
                    .Distinct()
                    .Order()
                    .ToArray();
                return new GovernedLoopNodeCatalogDescriptor(
                    node.Descriptor,
                    true,
                    true,
                    node.Descriptor.Kind == GovernedLoopNodeKind.Trigger,
                    terminals.Contains(node.Id),
                    outcomes,
                    outcomes,
                    GovernedLoopJoinPolicy.None,
                    0,
                    false,
                    null,
                    null,
                    node.Ports.Select(port => new GovernedLoopCatalogPortContract(port.Id, port.Direction, port.BindingKind, schemas[port.ValueSchemaId], port.Required)).ToArray(),
                    node.Parameters.Select(parameter => new GovernedLoopCatalogParameterContract(parameter.Key, GovernedLoopParameterValueKind.Text, true, 1, CustomLoopLimits.MaxGraphParameterValueCharacters, null, null, [])).ToArray(),
                    node.AuthorityCeiling.CapabilityIds,
                    new GovernedLoopNodeResourceBudget(0, 0, 0, 0));
            }).ToArray());
    }

    private static GovernedLoopAuthoritySnapshot Authority()
    {
        var role = RoleRevision();
        var pin = new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
        var lifecycle = new ContextualRoleLifecycleSnapshot(
            1,
            role.Identity.RoleId,
            role.Identity,
            ContextualRoleLifecycleState.Active,
            "publish-role",
            ContextualRoleRevisionMutationKind.Create,
            _now.AddMinutes(-10));
        return new GovernedLoopAuthoritySnapshot(
            true,
            Hash('d'),
            pin,
            role,
            lifecycle,
            _workspaceId,
            ContextualRoleInstructionSourceProbeStatus.Ready,
            role.PolicyMaxima.CapabilityIds,
            CustomLoopLimits.MaxGraphNodeAttempts,
            100_000,
            CustomLoopLimits.MaxGraphNodeEvidenceItems,
            100);
    }

    private static ContextualRoleRevisionPin RolePin()
    {
        var role = RoleRevision();
        return new ContextualRoleRevisionPin(role.Identity, role.ContentHash);
    }

    private static ContextualRoleRevision RoleRevision()
    {
        var role = new ContextualRoleRevision(
            1,
            new ContextualRoleRevisionIdentity("researcher", 1),
            string.Empty,
            "Researcher",
            "Research one bounded question.",
            ContextualRoleStatus.Published,
            new ContextualRoleProvenance("actor-1", _now.AddHours(-2), _now.AddHours(-1)),
            new ContextualRoleWorkspaceApplicability([_workspaceId]),
            new ContextualRoleInstructionSourceReference(
                ContextualRoleInstructionSourceKind.RoleArtifact,
                "researcher-source",
                ContextualRoleInstructionClassification.RoleInstruction),
            new ContextualRolePolicyMaxima(ImmutableArray.Create(ModelInferenceCapabilityId, WorkspaceReadCapabilityId)));
        return ContextualRoleRevisionContentHash.Apply(role);
    }

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("actor-1", out var actor, out _));
        return actor!;
    }

    private static string Hash(char value) => new(value, 64);

    private sealed class RecordingNodeCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public int Calls { get; private set; }

        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingAuthorityProvider(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public int Calls { get; private set; }

        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default)
        {
            _ = owningRole;
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public int Calls { get; private set; }

        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                GovernedLoopRevisionActorAuthorizationStatus.Authorized,
                request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                Hash('a')));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnusedRevisionStore : IGovernedLoopGraphRevisionStore
    {
        public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(GovernedLoopRevisionReference revision, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(string graphId, string operationId, string lifecycleRequestHash, string authoringRequestHash, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(GovernedLoopGraphRevisionStoreMutation mutation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedNodeCatalog : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedAuthorityProvider : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(ContextualRoleRevisionPin? owningRole, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class UnusedActorAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(GovernedLoopRevisionActorAuthorizationRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public int ExecuteCount { get; private set; }

        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return operation(cancellationToken);
        }

        public Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
