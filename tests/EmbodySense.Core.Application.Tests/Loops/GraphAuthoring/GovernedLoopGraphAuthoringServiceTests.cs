using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.GraphAuthoring;

public sealed class GovernedLoopGraphAuthoringServiceTests
{
    private static readonly DateTimeOffset _now = DateTimeOffset.Parse("2026-08-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Create_persists_payload_and_exact_replay_precedes_catalog_and_actor_authority()
    {
        var store = new InMemoryGraphStore();
        var authorizer = new StubAuthorizer();
        var candidate = Candidate("revision-1");
        var request = Authoring(Create("create-1", candidate), candidate);

        var committed = await Service(store, candidate, authorizer).MutateAsync(request);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, committed.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.Initial, committed.ChangeKind);
        Assert.Equal(candidate.RevisionId, committed.RevisionIdentity!.Revision.RevisionId);
        Assert.Matches("^[0-9a-f]{64}$", committed.RevisionIdentity.LayoutHash);
        Assert.Matches("^[0-9a-f]{64}$", committed.RevisionIdentity.ArtifactHash);
        Assert.Single(store.Snapshot(candidate.GraphId!)!.Artifacts);

        var denied = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var replayed = await Service(store, candidate, denied, catalogThrows: true).MutateAsync(request);

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Replayed, replayed.Status);
        Assert.Equal(committed.RevisionIdentity, replayed.RevisionIdentity);
        Assert.Equal(0, denied.Calls);
    }

    [Fact]
    public async Task Reusing_operation_with_changed_layout_conflicts_before_validation_or_authority()
    {
        var store = new InMemoryGraphStore();
        var first = Candidate("revision-1");
        var request = Authoring(Create("same-operation", first), first);
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, (await Service(store, first).MutateAsync(request)).Status);

        var changedLayout = Candidate("revision-1", displayDescription: "A different canvas-only description.");
        var denied = new StubAuthorizer { Status = GovernedLoopRevisionActorAuthorizationStatus.Denied };
        var conflict = await Service(store, changedLayout, denied, catalogThrows: true).MutateAsync(Authoring(request.LifecycleRequest, changedLayout));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Conflict, conflict.Status);
        Assert.Equal(0, denied.Calls);
    }

    [Fact]
    public async Task Replace_classifies_layout_only_and_executable_changes_as_new_revisions()
    {
        var store = new InMemoryGraphStore();
        var first = Candidate("revision-1");
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, (await Service(store, first).MutateAsync(Authoring(Create("create", first), first))).Status);

        var layout = Candidate("revision-2", displayDescription: "Moved for readability.");
        var layoutRequest = Replace("replace-layout", store.Snapshot(first.GraphId!)!, layout);
        var layoutResult = await Service(store, layout).MutateAsync(Authoring(layoutRequest, layout));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, layoutResult.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.LayoutOnly, layoutResult.ChangeKind);
        Assert.Equal(first.ExecutableHash(), layout.ExecutableHash());

        var executable = Candidate("revision-3", purpose: "Research one question with an explicit evidence check.");
        var executableRequest = Replace("replace-executable", store.Snapshot(first.GraphId!)!, executable);
        var executableResult = await Service(store, executable).MutateAsync(Authoring(executableRequest, executable));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, executableResult.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.Executable, executableResult.ChangeKind);
        Assert.Equal(3, store.Snapshot(first.GraphId!)!.Artifacts.Count);
    }

    [Fact]
    public async Task Current_graph_validation_rejection_precedes_actor_authorization_and_commit()
    {
        var store = new InMemoryGraphStore();
        var candidate = Candidate("revision-1");
        var authorizer = new StubAuthorizer();
        var result = await Service(store, candidate, authorizer, executableCatalog: false).MutateAsync(
            Authoring(Create("invalid-current-catalog", candidate), candidate));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.ValidationRejected, result.Status);
        Assert.Contains(result.GraphValidationErrors, error => error.Code == "node.descriptor.not-executable");
        Assert.Equal(0, authorizer.Calls);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task Publish_exact_loads_and_revalidates_the_stored_graph_payload()
    {
        var store = new InMemoryGraphStore();
        var candidate = Candidate("revision-1");
        await Service(store, candidate).MutateAsync(Authoring(Create("create", candidate), candidate));
        var publish = Publish("publish", store.Snapshot(candidate.GraphId!)!);

        var result = await Service(store, candidate).MutateAsync(Authoring(publish, null));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, result.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.LifecycleOnly, result.ChangeKind);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Published, result.LifecycleResult!.Head!.Status);
        Assert.NotNull(result.LifecycleResult.Head.PublishedRevision);
        Assert.Matches("^[0-9a-f]{64}$", result.GraphValidationEvidenceHash);
        Assert.True(store.ArtifactReads > 0);
    }

    [Fact]
    public async Task Optimistic_store_conflict_is_retried_under_shared_lifecycle_policy()
    {
        var store = new InMemoryGraphStore { ForcedStoreConflicts = 1 };
        var candidate = Candidate("revision-1");

        var result = await Service(store, candidate).MutateAsync(Authoring(Create("retry", candidate), candidate));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, result.Status);
        Assert.True(store.Commits >= 2);
        Assert.Single(store.Snapshot(candidate.GraphId!)!.Artifacts);
    }

    [Fact]
    public async Task Rollback_exact_copies_retained_publication_into_new_provenanced_successor()
    {
        var store = new InMemoryGraphStore();
        var first = Candidate("revision-1", purpose: "First executable behavior.");
        await Service(store, first).MutateAsync(Authoring(Create("create-1", first), first));
        var firstPublish = Publish("publish-1", store.Snapshot(first.GraphId!)!);
        await Service(store, first).MutateAsync(Authoring(firstPublish, null));
        var firstPin = store.Snapshot(first.GraphId!)!.Lifecycle.Head.PublishedRevision!;

        var second = Candidate("revision-2", purpose: "Second executable behavior.");
        await Service(store, second).MutateAsync(Authoring(Replace("replace-2", store.Snapshot(first.GraphId!)!, second), second));
        await Service(store, second).MutateAsync(Authoring(Publish("publish-2", store.Snapshot(first.GraphId!)!), null));

        var snapshot = store.Snapshot(first.GraphId!)!;
        var source = Assert.Single(snapshot.Artifacts, artifact => artifact.RevisionArtifact.Revision.RevisionId == "revision-1");
        var candidateRevision = GovernedLoopRevisionReference.Create(1, first.GraphId!, "revision-3", source.Graph.ExecutableHash);
        var rollback = Lifecycle(
            "rollback-1",
            GovernedLoopRevisionOperationKind.Rollback,
            snapshot,
            candidateRevision,
            snapshot.Lifecycle.Head.PublishedRevision!.Revision,
            firstPin);

        var result = await Service(store, first).MutateAsync(Authoring(rollback, null));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, result.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.RollbackCopy, result.ChangeKind);
        var rolledBack = Assert.Single(store.Snapshot(first.GraphId!)!.Artifacts, artifact => artifact.RevisionArtifact.Revision.RevisionId == "revision-3");
        Assert.Equal(source.Graph.ExecutableHash, rolledBack.Graph.ExecutableHash);
        Assert.Equal(source.LayoutHash, rolledBack.LayoutHash);
        Assert.Equal(firstPin, rolledBack.RevisionArtifact.RollbackSourcePublication);
    }

    [Fact]
    public async Task Disable_and_archive_change_lifecycle_without_rewriting_payloads()
    {
        var store = new InMemoryGraphStore();
        var candidate = Candidate("revision-1");
        await Service(store, candidate).MutateAsync(Authoring(Create("create", candidate), candidate));
        await Service(store, candidate).MutateAsync(Authoring(Publish("publish", store.Snapshot(candidate.GraphId!)!), null));
        var artifactHash = Assert.Single(store.Snapshot(candidate.GraphId!)!.Artifacts).ArtifactHash;

        var disabled = await Service(store, candidate).MutateAsync(Authoring(
            Lifecycle("disable", GovernedLoopRevisionOperationKind.Disable, store.Snapshot(candidate.GraphId!)!, target: candidate.Reference()),
            null));
        var archived = await Service(store, candidate).MutateAsync(Authoring(
            Lifecycle("archive", GovernedLoopRevisionOperationKind.Archive, store.Snapshot(candidate.GraphId!)!, target: candidate.Reference()),
            null));

        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, disabled.Status);
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Committed, archived.Status);
        Assert.Equal(GovernedLoopGraphRevisionChangeKind.LifecycleOnly, archived.ChangeKind);
        Assert.Equal(GovernedLoopRevisionLifecycleStatus.Archived, store.Snapshot(candidate.GraphId!)!.Lifecycle.Head.Status);
        Assert.Equal(artifactHash, Assert.Single(store.Snapshot(candidate.GraphId!)!.Artifacts).ArtifactHash);
    }

    [Fact]
    public async Task Missing_graph_payload_and_default_system_graph_fail_closed()
    {
        var store = new InMemoryGraphStore();
        var candidate = Candidate("revision-1");
        await Service(store, candidate).MutateAsync(Authoring(Create("create", candidate), candidate));
        store.RemoveGraphPayloads(candidate.GraphId!);

        var corrupted = await Service(store, candidate).MutateAsync(Authoring(Publish("publish", store.Snapshot(candidate.GraphId!)!), null));
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Ambiguous, corrupted.Status);

        var system = Candidate("revision-1") with { GraphId = "default-conversation" };
        var systemRevision = system.Reference();
        var systemRequest = new GovernedLoopRevisionLifecycleRequest(
            1,
            "system-write",
            GovernedLoopRevisionOperationKind.CreateDraft,
            system.GraphId!,
            Actor(),
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            systemRevision,
            null,
            null);
        var systemResult = await Service(new InMemoryGraphStore(), system).MutateAsync(Authoring(systemRequest, system));
        Assert.Equal(GovernedLoopGraphAuthoringStatus.Invalid, systemResult.Status);
        Assert.Contains(systemResult.GraphValidationErrors, error => error.Code == "graph.system-read-only");
    }

    private static GovernedLoopGraphAuthoringService Service(
        InMemoryGraphStore store,
        GovernedLoopGraphCandidate candidate,
        StubAuthorizer? authorizer = null,
        bool executableCatalog = true,
        bool catalogThrows = false)
    {
        IGovernedLoopNodeCatalog catalog = catalogThrows
            ? new ThrowingCatalog()
            : new FixedCatalog(new GovernedLoopNodeCatalogSnapshot(true, "catalog-1", Descriptors(candidate, executableCatalog)));
        var validation = new GovernedLoopGraphValidationService(catalog, new FixedAuthority(Authority()));
        return new GovernedLoopGraphAuthoringService(
            store,
            validation,
            authorizer ?? new StubAuthorizer(),
            new InlineAuthorityTransaction(),
            new FixedTimeProvider(_now));
    }

    private static GovernedLoopGraphAuthoringRequest Authoring(
        GovernedLoopRevisionLifecycleRequest lifecycle,
        GovernedLoopGraphCandidate? candidate)
        => new(1, lifecycle, candidate);

    private static GovernedLoopRevisionLifecycleRequest Create(string operationId, GovernedLoopGraphCandidate candidate)
        => new(
            1,
            operationId,
            GovernedLoopRevisionOperationKind.CreateDraft,
            candidate.GraphId!,
            Actor(),
            GovernedLoopRevisionLifecycleStatus.Unknown,
            0,
            null,
            null,
            candidate.Reference(),
            null,
            null);

    private static GovernedLoopRevisionLifecycleRequest Replace(
        string operationId,
        GovernedLoopGraphRevisionSnapshot snapshot,
        GovernedLoopGraphCandidate candidate)
        => Lifecycle(
            operationId,
            GovernedLoopRevisionOperationKind.ReplaceDraft,
            snapshot,
            candidate.Reference(),
            snapshot.Lifecycle.Head.DraftRevision ?? snapshot.Lifecycle.Head.PublishedRevision!.Revision);

    private static GovernedLoopRevisionLifecycleRequest Publish(
        string operationId,
        GovernedLoopGraphRevisionSnapshot snapshot)
        => Lifecycle(
            operationId,
            GovernedLoopRevisionOperationKind.Publish,
            snapshot,
            target: snapshot.Lifecycle.Head.DraftRevision);

    private static GovernedLoopRevisionLifecycleRequest Lifecycle(
        string operationId,
        GovernedLoopRevisionOperationKind kind,
        GovernedLoopGraphRevisionSnapshot snapshot,
        GovernedLoopRevisionReference? candidate = null,
        GovernedLoopRevisionReference? target = null,
        GovernedLoopRevisionPublicationPin? source = null)
    {
        var head = snapshot.Lifecycle.Head;
        return new GovernedLoopRevisionLifecycleRequest(
            1,
            operationId,
            kind,
            head.GraphId,
            Actor(),
            head.Status,
            head.LifecycleVersion,
            head.DraftRevision,
            head.PublishedRevision,
            candidate,
            target,
            source);
    }

    private static GovernedLoopGraphCandidate Candidate(
        string revisionId,
        string purpose = "Research one question safely.",
        string displayDescription = "Display only.")
        => new(
            1,
            "research-loop",
            revisionId,
            purpose,
            "researcher",
            "trigger",
            ["exit"],
            GovernedLoopAuthorityCeiling.Create(["model-inference", "workspace-read"]),
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
                displayDescription,
                [
                    new GovernedLoopNodeDisplayMetadata("trigger", "Trigger", "Start.", 0, 0),
                    new GovernedLoopNodeDisplayMetadata("infer", "Inference", "Answer.", 100, 0),
                    new GovernedLoopNodeDisplayMetadata("exit", "Exit", "Finish.", 200, 0),
                ]));

    private static GovernedLoopNodeDefinition[] Nodes()
        =>
        [
            new("trigger", new(GovernedLoopNodeKind.Trigger, "manual-trigger", 1), [Port("request", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Context)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
            new("infer", new(GovernedLoopNodeKind.Inference, "provider-inference", 1), [Port("request", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("invocation-context", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Context), Port("result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create(["model-inference"]), new Dictionary<string, string> { ["instruction"] = "Answer safely." }),
            new("exit", new(GovernedLoopNodeKind.Exit, "success-exit", 1), [Port("result", GovernedLoopPortDirection.Input, GovernedLoopBindingKind.Data), Port("published-result", GovernedLoopPortDirection.Output, GovernedLoopBindingKind.Data)], GovernedLoopAuthorityCeiling.Create([]), new Dictionary<string, string>()),
        ];

    private static GovernedLoopPortDefinition Port(string id, GovernedLoopPortDirection direction, GovernedLoopBindingKind kind)
        => new(id, direction, kind, "text", true);

    private static GovernedLoopNodeCatalogDescriptor[] Descriptors(
        GovernedLoopGraphCandidate candidate,
        bool executable)
    {
        var schemas = candidate.ValueSchemas!.Cast<GovernedLoopValueSchemaDefinition>().ToDictionary(schema => schema.Id, schema => schema.Kind, StringComparer.Ordinal);
        var terminals = candidate.TerminalNodeIds!.Cast<string>().ToHashSet(StringComparer.Ordinal);
        return candidate.Nodes!.Cast<GovernedLoopNodeDefinition>().Select(node =>
        {
            var outcomes = candidate.ControlEdges!.Where(edge => edge!.FromNodeId == node.Id).Select(edge => edge!.Condition).Distinct().Order().ToArray();
            return new GovernedLoopNodeCatalogDescriptor(
                node.Descriptor,
                true,
                executable,
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
        }).ToArray();
    }

    private static GovernedLoopAuthoritySnapshot Authority()
        => new(true, "authority-1", "researcher", ["model-inference", "workspace-read"], CustomLoopLimits.MaxGraphNodeAttempts, 100_000, CustomLoopLimits.MaxGraphNodeEvidenceItems, 100);

    private static AuthorityActorId Actor()
    {
        Assert.True(AuthorityActorId.TryParse("actor-1", out var actor, out _));
        return actor!;
    }

    private static string Hash(char value) => new(value, 64);

    private sealed class FixedCatalog(GovernedLoopNodeCatalogSnapshot snapshot) : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class ThrowingCatalog : IGovernedLoopNodeCatalog
    {
        public Task<GovernedLoopNodeCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => throw new IOException("catalog unavailable");
    }

    private sealed class FixedAuthority(GovernedLoopAuthoritySnapshot snapshot) : IGovernedLoopAuthoritySnapshotProvider
    {
        public Task<GovernedLoopAuthoritySnapshot> GetSnapshotAsync(string roleId, CancellationToken cancellationToken = default) => Task.FromResult(snapshot);
    }

    private sealed class StubAuthorizer : IGovernedLoopRevisionActorAuthorizer
    {
        internal int Calls { get; private set; }
        internal GovernedLoopRevisionActorAuthorizationStatus Status { get; set; } = GovernedLoopRevisionActorAuthorizationStatus.Authorized;

        public Task<GovernedLoopRevisionActorAuthorization> AuthorizeAsync(
            GovernedLoopRevisionActorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new GovernedLoopRevisionActorAuthorization(
                Status,
                request.Request.OperationId,
                request.RequestHash,
                request.Request.ActorId,
                Hash('a')));
        }
    }

    private sealed class InlineAuthorityTransaction : ICapabilityAuthorityTransaction
    {
        public Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            => operation(cancellationToken);

        public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(
            Func<CancellationToken, Task<bool>> validator,
            CancellationToken cancellationToken = default)
            => await validator(cancellationToken) ? new StubLease() : null;
    }

    private sealed class StubLease : ICapabilityAuthorityLease
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryGraphStore : IGovernedLoopGraphRevisionStore
    {
        private readonly Dictionary<string, GovernedLoopGraphRevisionSnapshot> _graphs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GovernedLoopGraphRevisionStoredOperation> _operations = new(StringComparer.Ordinal);

        internal long Generation { get; private set; }
        internal int Commits { get; private set; }
        internal int ArtifactReads { get; private set; }
        internal int ForcedStoreConflicts { get; set; }

        internal GovernedLoopGraphRevisionSnapshot? Snapshot(string graphId)
            => _graphs.TryGetValue(graphId, out var snapshot) ? snapshot : null;

        internal void RemoveGraphPayloads(string graphId)
        {
            var snapshot = _graphs[graphId];
            _graphs[graphId] = snapshot with { Artifacts = Array.Empty<GovernedLoopGraphRevisionArtifact>() };
        }

        public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(
            string graphId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_graphs.TryGetValue(graphId, out var snapshot)
                ? new GovernedLoopGraphRevisionReadResult(GovernedLoopRevisionStoreReadStatus.Ready, Generation, snapshot)
                : new GovernedLoopGraphRevisionReadResult(GovernedLoopRevisionStoreReadStatus.NotFound, Generation, null));

        public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(
            GovernedLoopRevisionReference revision,
            CancellationToken cancellationToken = default)
        {
            ArtifactReads++;
            var artifact = _graphs.TryGetValue(revision.GraphId, out var snapshot)
                ? snapshot.Artifacts.SingleOrDefault(candidate => Same(candidate.RevisionArtifact.Revision, revision))
                : null;
            return Task.FromResult(artifact is null
                ? new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.NotFound, Generation, null)
                : new GovernedLoopGraphRevisionArtifactReadResult(GovernedLoopRevisionStoreReadStatus.Ready, Generation, artifact));
        }

        public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(
            string graphId,
            string operationId,
            string lifecycleRequestHash,
            string authoringRequestHash,
            CancellationToken cancellationToken = default)
        {
            _operations.TryGetValue(operationId, out var operation);
            return Task.FromResult(_graphs.TryGetValue(graphId, out var snapshot)
                ? new GovernedLoopGraphRevisionMutationReadResult(GovernedLoopRevisionStoreReadStatus.Ready, Generation, snapshot, operation)
                : new GovernedLoopGraphRevisionMutationReadResult(GovernedLoopRevisionStoreReadStatus.NotFound, Generation, null, operation));
        }

        public Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(
            GovernedLoopGraphRevisionStoreMutation mutation,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            if (ForcedStoreConflicts > 0)
            {
                ForcedStoreConflicts--;
                Generation++;
                return Task.FromResult(new GovernedLoopGraphRevisionCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                    Generation,
                    null,
                    null));
            }

            var lifecycle = mutation.LifecycleMutation;
            if (_operations.TryGetValue(lifecycle.Operation.OperationId, out var existing))
            {
                _graphs.TryGetValue(lifecycle.GraphId, out var existingSnapshot);
                var exact = string.Equals(existing.AuthoringRequestHash, mutation.AuthoringRequestHash, StringComparison.Ordinal);
                return Task.FromResult(new GovernedLoopGraphRevisionCommitResult(
                    exact ? GovernedLoopRevisionStoreCommitStatus.Replayed : GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                    Generation,
                    existing,
                    existingSnapshot));
            }

            if (lifecycle.ExpectedStoreGeneration != Generation)
            {
                return Task.FromResult(new GovernedLoopGraphRevisionCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                    Generation,
                    null,
                    null));
            }

            _graphs.TryGetValue(lifecycle.GraphId, out var current);
            GovernedLoopGraphRevisionSnapshot? next = null;
            if (lifecycle.HeadToWrite is not null)
            {
                var lifecycleArtifacts = current?.Lifecycle.Artifacts.ToList() ?? [];
                var graphArtifacts = current?.Artifacts.ToList() ?? [];
                if (lifecycle.ArtifactToAppend is not null)
                {
                    lifecycleArtifacts.Add(lifecycle.ArtifactToAppend);
                    graphArtifacts.Add(GovernedLoopGraphRevisionArtifactFactory.Create(1, lifecycle.ArtifactToAppend, mutation.GraphToAppend!));
                }

                var operations = current?.Lifecycle.Operations.ToList() ?? [];
                operations.Add(lifecycle.Operation);
                next = new GovernedLoopGraphRevisionSnapshot(
                    new GovernedLoopRevisionStoreSnapshot(
                        lifecycle.HeadToWrite,
                        Array.AsReadOnly(lifecycleArtifacts.ToArray()),
                        Array.AsReadOnly(operations.ToArray())),
                    Array.AsReadOnly(graphArtifacts.ToArray()));
                _graphs[lifecycle.GraphId] = next;
            }
            else if (current is not null)
            {
                var operations = current.Lifecycle.Operations.Append(lifecycle.Operation).ToArray();
                next = current with
                {
                    Lifecycle = current.Lifecycle with { Operations = Array.AsReadOnly(operations) },
                };
                _graphs[lifecycle.GraphId] = next;
            }

            var stored = new GovernedLoopGraphRevisionStoredOperation(
                GovernedLoopGraphRevisionOperationState.Terminal,
                lifecycle.GraphId,
                lifecycle.Operation.OperationId,
                lifecycle.Operation.RequestHash,
                mutation.AuthoringRequestHash,
                new GovernedLoopRevisionStoredOperation(lifecycle.GraphId, lifecycle.Operation),
                mutation.GraphValidationEvidenceHash);
            _operations.Add(lifecycle.Operation.OperationId, stored);
            Generation++;
            return Task.FromResult(new GovernedLoopGraphRevisionCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Committed,
                Generation,
                stored,
                next));
        }

        private static bool Same(GovernedLoopRevisionReference left, GovernedLoopRevisionReference right)
            => left.SchemaVersion == right.SchemaVersion
                && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
                && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
                && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);
    }
}

internal static class GovernedLoopGraphCandidateTestExtensions
{
    internal static GovernedLoopGraphDefinition Normalize(this GovernedLoopGraphCandidate candidate)
        => GovernedLoopGraphNormalizer.Normalize(candidate).Graph!;

    internal static GovernedLoopRevisionReference Reference(this GovernedLoopGraphCandidate candidate)
        => candidate.Normalize().RevisionReference;

    internal static string ExecutableHash(this GovernedLoopGraphCandidate candidate)
        => candidate.Normalize().ExecutableHash;
}
