using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring;

/// <summary>Persists authenticated immutable graph payloads before delegating their generic lifecycle commit.</summary>
/// <remarks>
/// Payload and full-intent files are immutable, exact-path evidence. The generic lifecycle remains the sole visibility
/// authority: orphan files left before lifecycle publication are inert, and a lifecycle artifact is never returned
/// without its exact authenticated graph payload. All filesystem traversal is retained-handle, no-follow, bounded,
/// and serialized with the shared workspace authority transaction plus one graph-authoring lock.
/// </remarks>
public sealed class GovernedLoopGraphRevisionStore : IGovernedLoopGraphRevisionStore
{
    private static readonly string _emptyTrustDigest = CapabilityIntegrityDigest.Compute(
        Encoding.UTF8.GetBytes("embodysense-governed-loop-graph-authoring-empty-v1\n")).Value;
    private readonly GovernedLoopGraphRevisionStorePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly IGovernedLoopRevisionLifecycleStore _lifecycleStore;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly GovernedLoopGraphRevisionStoreOptions _options;

    /// <summary>Creates a graph-revision store with the default server-owned trust provider.</summary>
    public GovernedLoopGraphRevisionStore(
        WorkspacePaths paths,
        IGovernedLoopRevisionLifecycleStore lifecycleStore,
        GovernedLoopGraphRevisionStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(
            paths,
            lifecycleStore,
            FileCapabilityCatalogTrustProvider.CreateDefault(),
            options,
            durabilityBarrier,
            authorityTransaction)
    {
    }

    /// <summary>Creates a graph-revision store over an explicit server-owned trust provider.</summary>
    public GovernedLoopGraphRevisionStore(
        WorkspacePaths paths,
        IGovernedLoopRevisionLifecycleStore lifecycleStore,
        ICapabilityCatalogTrustProvider trustProvider,
        GovernedLoopGraphRevisionStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lifecycleStore);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new GovernedLoopGraphRevisionStoreOptions());
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > _options.MaxIntentUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The graph-authoring trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _paths = new GovernedLoopGraphRevisionStorePaths(paths);
        _pathGuard = new CapabilityCatalogPathGuard(
            paths.RootPath,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver);
        _lifecycleStore = lifecycleStore;
        _trustProvider = trustProvider;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public Task<GovernedLoopGraphRevisionReadResult> ReadGraphAsync(
        string graphId,
        CancellationToken cancellationToken = default)
    {
        if (!IsMutableGraphId(graphId))
        {
            return Task.FromResult(GraphRead(GovernedLoopRevisionStoreReadStatus.Unavailable));
        }

        return ReadUnderAuthorityAsync(
            token => ReadGraphCoreAsync(graphId, token),
            GraphRead,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactAsync(
        GovernedLoopRevisionReference revision,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidRevision(revision) || !IsMutableGraphId(revision.GraphId))
        {
            return Task.FromResult(ArtifactRead(GovernedLoopRevisionStoreReadStatus.Unavailable));
        }

        return ReadUnderAuthorityAsync(
            token => ReadArtifactCoreAsync(revision, token),
            ArtifactRead,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsMutableGraphId(graphId)
            || !IsIdentifier(operationId)
            || !IsHash(lifecycleRequestHash)
            || !IsHash(authoringRequestHash))
        {
            return Task.FromResult(MutationRead(GovernedLoopRevisionStoreReadStatus.Unavailable));
        }

        return ReadUnderAuthorityAsync(
            token => ReadForMutationCoreAsync(graphId, operationId, lifecycleRequestHash, authoringRequestHash, token),
            MutationRead,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopGraphRevisionCommitResult> CommitAsync(
        GovernedLoopGraphRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        GovernedLoopGraphDefinition? proposedGraph;
        try
        {
            proposedGraph = ValidateMutation(mutation);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Commit(GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }

        var callbackEntered = false;
        var durableWorkStarted = false;
        GovernedLoopGraphRevisionCommitResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await CommitCoreAsync(
                        mutation,
                        proposedGraph,
                        () => durableWorkStarted = true,
                        token);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (callbackResult is not null)
            {
                return callbackResult;
            }

            if (!callbackEntered && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return Commit(durableWorkStarted
                ? GovernedLoopRevisionStoreCommitStatus.Ambiguous
                : GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult
                ?? Commit(durableWorkStarted
                    ? GovernedLoopRevisionStoreCommitStatus.Ambiguous
                    : GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopGraphRevisionReadResult> ReadGraphCoreAsync(
        string graphId,
        CancellationToken cancellationToken)
    {
        var lifecycle = await _lifecycleStore.ReadGraphAsync(graphId, cancellationToken);
        if (lifecycle.Status != GovernedLoopRevisionStoreReadStatus.Ready || lifecycle.Snapshot is null)
        {
            return new GovernedLoopGraphRevisionReadResult(lifecycle.Status, lifecycle.StoreGeneration, null);
        }

        try
        {
            await using var session = await AcquireExistingAsync(cancellationToken);
            if (session is null)
            {
                return new GovernedLoopGraphRevisionReadResult(
                    GovernedLoopRevisionStoreReadStatus.Ambiguous,
                    lifecycle.StoreGeneration,
                    null);
            }
            var shape = InspectStore(session);
            await RequireDiscoveredArtifactsAsync(
                session,
                WorkspaceIdentity(session),
                shape.ArtifactIdentities,
                cancellationToken);
            await RequireReadableTrustPostureAsync(
                session,
                WorkspaceIdentity(session),
                shape.IntentIds,
                cancellationToken);
            var snapshot = await LoadSnapshotAsync(session, lifecycle.Snapshot, cancellationToken);
            return new GovernedLoopGraphRevisionReadResult(
                GovernedLoopRevisionStoreReadStatus.Ready,
                lifecycle.StoreGeneration,
                snapshot);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new GovernedLoopGraphRevisionReadResult(
                GovernedLoopRevisionStoreReadStatus.Ambiguous,
                lifecycle.StoreGeneration,
                null);
        }
    }

    private async Task<GovernedLoopGraphRevisionArtifactReadResult> ReadArtifactCoreAsync(
        GovernedLoopRevisionReference revision,
        CancellationToken cancellationToken)
    {
        var lifecycle = await _lifecycleStore.ReadGraphAsync(revision.GraphId, cancellationToken);
        if (lifecycle.Status != GovernedLoopRevisionStoreReadStatus.Ready || lifecycle.Snapshot is null)
        {
            return new GovernedLoopGraphRevisionArtifactReadResult(lifecycle.Status, lifecycle.StoreGeneration, null);
        }

        var lifecycleArtifact = lifecycle.Snapshot.Artifacts.SingleOrDefault(
            artifact => SameReference(artifact.Revision, revision));
        if (lifecycleArtifact is null)
        {
            return new GovernedLoopGraphRevisionArtifactReadResult(
                GovernedLoopRevisionStoreReadStatus.NotFound,
                lifecycle.StoreGeneration,
                null);
        }

        try
        {
            await using var session = await AcquireExistingAsync(cancellationToken);
            if (session is null)
            {
                return new GovernedLoopGraphRevisionArtifactReadResult(
                    GovernedLoopRevisionStoreReadStatus.Ambiguous,
                    lifecycle.StoreGeneration,
                    null);
            }
            var shape = InspectStore(session);
            await RequireDiscoveredArtifactsAsync(
                session,
                WorkspaceIdentity(session),
                shape.ArtifactIdentities,
                cancellationToken);
            await RequireReadableTrustPostureAsync(
                session,
                WorkspaceIdentity(session),
                shape.IntentIds,
                cancellationToken);
            var artifact = await LoadArtifactAsync(session, lifecycleArtifact, cancellationToken);
            return new GovernedLoopGraphRevisionArtifactReadResult(
                artifact is null
                    ? GovernedLoopRevisionStoreReadStatus.Ambiguous
                    : GovernedLoopRevisionStoreReadStatus.Ready,
                lifecycle.StoreGeneration,
                artifact);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new GovernedLoopGraphRevisionArtifactReadResult(
                GovernedLoopRevisionStoreReadStatus.Ambiguous,
                lifecycle.StoreGeneration,
                null);
        }
    }

    private async Task<GovernedLoopGraphRevisionMutationReadResult> ReadForMutationCoreAsync(
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        CancellationToken cancellationToken)
    {
        var lifecycle = await _lifecycleStore.ReadForMutationAsync(
            graphId,
            operationId,
            lifecycleRequestHash,
            cancellationToken);
        if (lifecycle.Status is GovernedLoopRevisionStoreReadStatus.Unavailable or GovernedLoopRevisionStoreReadStatus.Ambiguous)
        {
            return new GovernedLoopGraphRevisionMutationReadResult(lifecycle.Status, lifecycle.StoreGeneration, null, null);
        }

        CapabilityCatalogPathSession? session = null;
        try
        {
            session = await AcquireExistingAsync(cancellationToken);
            if (session is null)
            {
                return lifecycle.ExistingOperation is null && lifecycle.Snapshot is null
                    ? new GovernedLoopGraphRevisionMutationReadResult(lifecycle.Status, lifecycle.StoreGeneration, null, null)
                    : MutationRead(GovernedLoopRevisionStoreReadStatus.Ambiguous);
            }

            var shape = InspectStore(session);
            await RequireDiscoveredArtifactsAsync(
                session,
                WorkspaceIdentity(session),
                shape.ArtifactIdentities,
                cancellationToken);
            await RequireReadableTrustPostureAsync(
                session,
                WorkspaceIdentity(session),
                shape.IntentIds,
                cancellationToken);
            var intent = await LoadIntentAsync(session, operationId, cancellationToken);
            GovernedLoopGraphRevisionSnapshot? snapshot = null;
            if (lifecycle.Snapshot is not null)
            {
                snapshot = await LoadSnapshotAsync(session, lifecycle.Snapshot, cancellationToken);
            }

            var operation = lifecycle.ExistingOperation is not null
                ? TerminalOperation(intent, lifecycle.ExistingOperation)
                : intent is null
                    ? null
                    : PendingOperation(intent);
            return new GovernedLoopGraphRevisionMutationReadResult(
                lifecycle.Status,
                lifecycle.StoreGeneration,
                snapshot,
                operation);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return MutationRead(GovernedLoopRevisionStoreReadStatus.Ambiguous);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    private async Task<GovernedLoopGraphRevisionCommitResult> CommitCoreAsync(
        GovernedLoopGraphRevisionStoreMutation mutation,
        GovernedLoopGraphDefinition? proposedGraph,
        Action markDurableWorkStarted,
        CancellationToken cancellationToken)
    {
        await using var session = await AcquireAsync(cancellationToken);
        var shape = InspectStore(session);
        var identity = WorkspaceIdentity(session);
        await RequireDiscoveredArtifactsAsync(
            session,
            identity,
            shape.ArtifactIdentities,
            cancellationToken);
        await ReconcilePendingTrustAsync(session, identity, shape.IntentIds, cancellationToken);
        var lifecycleMutation = mutation.LifecycleMutation;
        var operationId = lifecycleMutation.Operation.OperationId;
        var proposedPayloadHash = proposedGraph is null
            ? null
            : GovernedLoopGraphRevisionStoreJson.ComputePayloadHash(proposedGraph);
        var lifecycleRead = await _lifecycleStore.ReadForMutationAsync(
            lifecycleMutation.GraphId,
            operationId,
            lifecycleMutation.Operation.RequestHash,
            cancellationToken);
        if (lifecycleRead.Status is GovernedLoopRevisionStoreReadStatus.Unavailable or GovernedLoopRevisionStoreReadStatus.Ambiguous)
        {
            return Commit(lifecycleRead.Status == GovernedLoopRevisionStoreReadStatus.Ambiguous
                ? GovernedLoopRevisionStoreCommitStatus.Ambiguous
                : GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }

        var existingIntent = await LoadIntentAsync(session, operationId, cancellationToken);
        if (lifecycleRead.ExistingOperation is not null)
        {
            if (existingIntent is null)
            {
                return Commit(GovernedLoopRevisionStoreCommitStatus.Ambiguous);
            }

            var stored = TerminalOperation(existingIntent, lifecycleRead.ExistingOperation);
            var exact = IntentMatches(existingIntent, mutation, proposedPayloadHash)
                && string.Equals(lifecycleRead.ExistingOperation.GraphId, lifecycleMutation.GraphId, StringComparison.Ordinal)
                && string.Equals(lifecycleRead.ExistingOperation.Evidence.RequestHash, lifecycleMutation.Operation.RequestHash, StringComparison.Ordinal);
            GovernedLoopGraphRevisionSnapshot? snapshot = null;
            if (lifecycleRead.Snapshot is not null)
            {
                snapshot = await LoadSnapshotAsync(session, lifecycleRead.Snapshot, cancellationToken);
            }

            return new GovernedLoopGraphRevisionCommitResult(
                exact
                    ? GovernedLoopRevisionStoreCommitStatus.Replayed
                    : GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                lifecycleRead.StoreGeneration,
                stored,
                snapshot);
        }

        if (lifecycleRead.StoreGeneration != lifecycleMutation.ExpectedStoreGeneration)
        {
            var snapshot = lifecycleRead.Snapshot is null
                ? null
                : await LoadSnapshotAsync(session, lifecycleRead.Snapshot, cancellationToken);
            return new GovernedLoopGraphRevisionCommitResult(
                GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                lifecycleRead.StoreGeneration,
                null,
                snapshot);
        }

        if (existingIntent is not null && !IntentMatches(existingIntent, mutation, proposedPayloadHash))
        {
            return new GovernedLoopGraphRevisionCommitResult(
                GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                lifecycleRead.StoreGeneration,
                null,
                lifecycleRead.Snapshot is null
                    ? null
                    : await LoadSnapshotAsync(session, lifecycleRead.Snapshot, cancellationToken));
        }

        if (existingIntent is null)
        {
            var artifactPath = proposedGraph is null
                ? null
                : _paths.ArtifactPath(proposedGraph.GraphId, proposedGraph.RevisionId);
            var reservesArtifact = artifactPath is not null && !session.FileExists(artifactPath);
            var reservesIntent = !session.FileExists(_paths.OperationPath(operationId));
            var reservesGraphDirectory = proposedGraph is not null
                && !session.DirectoryExists(Path.Combine(_paths.ArtifactsPath, proposedGraph.GraphId));

            GovernedLoopGraphRevisionArtifactDocument? existingArtifact = null;
            if (proposedGraph is not null && !reservesArtifact)
            {
                existingArtifact = await LoadUnpublishedArtifactAsync(session, proposedGraph, cancellationToken);
                if (existingArtifact is null
                    || !string.Equals(existingArtifact.PayloadHash, proposedPayloadHash, StringComparison.Ordinal))
                {
                    return new GovernedLoopGraphRevisionCommitResult(
                        GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                        lifecycleRead.StoreGeneration,
                        null,
                        lifecycleRead.Snapshot is null
                            ? null
                            : await LoadSnapshotAsync(session, lifecycleRead.Snapshot, cancellationToken));
                }

                markDurableWorkStarted();
                await session.WriteBytesImmutablyAsync(
                    artifactPath!,
                    GovernedLoopGraphRevisionStoreJson.Serialize(existingArtifact),
                    cancellationToken);
            }

            var trust = await ReadOrInitializeTrustAsync(identity, cancellationToken);
            var trustGeneration = checked(trust.CurrentGeneration + 1);
            GovernedLoopGraphRevisionArtifactDocument? artifactDocument = null;
            byte[]? artifactBytes = null;
            if (proposedGraph is not null && existingArtifact is null)
            {
                artifactDocument = await SignArtifactAsync(
                    proposedGraph,
                    identity,
                    trustGeneration,
                    cancellationToken);
                artifactBytes = GovernedLoopGraphRevisionStoreJson.Serialize(artifactDocument);
                RequireArtifactSize(artifactBytes);
            }

            var intentDocument = await SignIntentAsync(
                mutation,
                proposedPayloadHash,
                identity,
                trustGeneration,
                cancellationToken);
            var intentBytes = GovernedLoopGraphRevisionStoreJson.Serialize(intentDocument);
            RequireIntentSize(intentBytes);
            var plannedWrites = new List<ImmutableWritePlan>(2);
            if (artifactBytes is not null)
            {
                plannedWrites.Add(new ImmutableWritePlan(artifactPath!, artifactBytes));
            }
            plannedWrites.Add(new ImmutableWritePlan(_paths.OperationPath(operationId), intentBytes));
            await RequireCapacityAsync(
                session,
                shape,
                reservesArtifact,
                reservesIntent,
                reservesGraphDirectory,
                plannedWrites,
                cancellationToken);
            if (artifactDocument is not null)
            {
                markDurableWorkStarted();
                await session.WriteBytesImmutablyAsync(artifactPath!, artifactBytes!, cancellationToken);
                await ObserveAsync(GovernedLoopGraphRevisionPersistenceBoundary.ArtifactPublished, cancellationToken);
            }

            markDurableWorkStarted();
            await session.WriteBytesImmutablyAsync(_paths.OperationPath(operationId), intentBytes, cancellationToken);
            await ObserveAsync(GovernedLoopGraphRevisionPersistenceBoundary.IntentPublished, cancellationToken);
            var advanced = await _trustProvider.AdvanceAsync(
                identity,
                trust.CurrentGeneration,
                trust.CurrentContentDigest,
                trustGeneration,
                intentDocument.ContentDigest,
                cancellationToken);
            RequireExactTrustAdvance(advanced, identity, trust, trustGeneration, intentDocument.ContentDigest);
            await ObserveAsync(GovernedLoopGraphRevisionPersistenceBoundary.TrustAdvanced, cancellationToken);
            existingIntent = intentDocument;
        }
        else
        {
            markDurableWorkStarted();
            await RequireExistingPayloadAsync(session, existingIntent, proposedGraph, cancellationToken);
            await ReconcileIntentTrustAsync(existingIntent, cancellationToken);
        }

        await ObserveAsync(GovernedLoopGraphRevisionPersistenceBoundary.LifecycleCommitStarting, cancellationToken);
        var committed = await _lifecycleStore.CommitAsync(lifecycleMutation, cancellationToken);
        GovernedLoopGraphRevisionSnapshot? committedSnapshot = null;
        GovernedLoopGraphRevisionStoredOperation? committedOperation = null;
        if (committed.Snapshot is not null)
        {
            committedSnapshot = await LoadSnapshotAsync(session, committed.Snapshot, cancellationToken);
        }
        if (committed.Operation is not null)
        {
            committedOperation = TerminalOperation(existingIntent, committed.Operation);
        }

        if (committed.Status is GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed
            && (committedOperation is null || committedSnapshot is null))
        {
            return Commit(GovernedLoopRevisionStoreCommitStatus.Ambiguous);
        }

        return new GovernedLoopGraphRevisionCommitResult(
            committed.Status,
            committed.StoreGeneration,
            committedOperation,
            committedSnapshot);
    }

    private async Task<GovernedLoopGraphRevisionSnapshot> LoadSnapshotAsync(
        CapabilityCatalogPathSession session,
        GovernedLoopRevisionStoreSnapshot lifecycle,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<GovernedLoopGraphRevisionArtifact>(lifecycle.Artifacts.Count);
        foreach (var lifecycleArtifact in lifecycle.Artifacts)
        {
            var artifact = await LoadArtifactAsync(session, lifecycleArtifact, cancellationToken)
                ?? throw new FormatException("A visible governed-loop lifecycle revision has no immutable graph payload.");
            artifacts.Add(artifact);
        }

        return new GovernedLoopGraphRevisionSnapshot(lifecycle, Array.AsReadOnly(artifacts.ToArray()));
    }

    private async Task<GovernedLoopGraphRevisionArtifact?> LoadArtifactAsync(
        CapabilityCatalogPathSession session,
        GovernedLoopRevisionArtifact lifecycleArtifact,
        CancellationToken cancellationToken)
    {
        var path = _paths.ArtifactPath(
            lifecycleArtifact.Revision.GraphId,
            lifecycleArtifact.Revision.RevisionId);
        var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaxArtifactUtf8Bytes, cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        var document = GovernedLoopGraphRevisionStoreJson.DeserializeArtifact(bytes);
        await RequireTrustedAsync(document, WorkspaceIdentity(session), cancellationToken);
        if (!SameReference(document.Graph.RevisionReference, lifecycleArtifact.Revision))
        {
            throw new FormatException("The immutable graph payload does not match its visible lifecycle artifact.");
        }

        return GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            lifecycleArtifact,
            document.Graph);
    }

    private async Task<GovernedLoopGraphRevisionArtifactDocument?> LoadUnpublishedArtifactAsync(
        CapabilityCatalogPathSession session,
        GovernedLoopGraphDefinition proposedGraph,
        CancellationToken cancellationToken)
    {
        var bytes = await session.TryReadAllBytesBoundAsync(
            _paths.ArtifactPath(proposedGraph.GraphId, proposedGraph.RevisionId),
            _options.MaxArtifactUtf8Bytes,
            cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        var document = GovernedLoopGraphRevisionStoreJson.DeserializeArtifact(bytes);
        await RequireTrustedAsync(document, WorkspaceIdentity(session), cancellationToken);
        return document;
    }

    private async Task<GovernedLoopGraphRevisionIntentDocument?> LoadIntentAsync(
        CapabilityCatalogPathSession session,
        string operationId,
        CancellationToken cancellationToken)
    {
        var bytes = await session.TryReadAllBytesBoundAsync(
            _paths.OperationPath(operationId),
            _options.MaxIntentUtf8Bytes,
            cancellationToken);
        if (bytes is null)
        {
            return null;
        }

        var intent = GovernedLoopGraphRevisionStoreJson.DeserializeIntent(bytes);
        await RequireTrustedAsync(intent, WorkspaceIdentity(session), cancellationToken);
        if (!string.Equals(intent.OperationId, operationId, StringComparison.Ordinal))
        {
            throw new FormatException("The immutable graph-authoring intent path does not match its operation identity.");
        }
        return intent;
    }

    private async Task RequireExistingPayloadAsync(
        CapabilityCatalogPathSession session,
        GovernedLoopGraphRevisionIntentDocument intent,
        GovernedLoopGraphDefinition? proposedGraph,
        CancellationToken cancellationToken)
    {
        if (proposedGraph is null)
        {
            if (intent.GraphPayloadHash is not null)
            {
                throw new IOException("The existing graph-authoring intent is bound to a graph payload.");
            }
            return;
        }

        var bytes = await session.TryReadAllBytesBoundAsync(
            _paths.ArtifactPath(proposedGraph.GraphId, proposedGraph.RevisionId),
            _options.MaxArtifactUtf8Bytes,
            cancellationToken) ?? throw new IOException("The exact graph-authoring retry is missing its immutable payload.");
        var document = GovernedLoopGraphRevisionStoreJson.DeserializeArtifact(bytes);
        await RequireTrustedAsync(document, WorkspaceIdentity(session), cancellationToken);
        var proposedPayloadHash = GovernedLoopGraphRevisionStoreJson.ComputePayloadHash(proposedGraph);
        if (!string.Equals(intent.GraphPayloadHash, proposedPayloadHash, StringComparison.Ordinal)
            || !string.Equals(document.PayloadHash, proposedPayloadHash, StringComparison.Ordinal))
        {
            throw new IOException("The exact graph-authoring retry changed immutable payload bytes or identity.");
        }
    }

    private static GovernedLoopGraphRevisionStoredOperation TerminalOperation(
        GovernedLoopGraphRevisionIntentDocument? intent,
        GovernedLoopRevisionStoredOperation operation)
    {
        if (intent is null
            || !string.Equals(intent.GraphId, operation.GraphId, StringComparison.Ordinal)
            || !string.Equals(intent.OperationId, operation.Evidence.OperationId, StringComparison.Ordinal)
            || !string.Equals(intent.LifecycleRequestHash, operation.Evidence.RequestHash, StringComparison.Ordinal))
        {
            throw new FormatException("The visible lifecycle operation has no exact authenticated graph-authoring intent.");
        }

        return new GovernedLoopGraphRevisionStoredOperation(
            GovernedLoopGraphRevisionOperationState.Terminal,
            intent.GraphId,
            intent.OperationId,
            intent.LifecycleRequestHash,
            intent.AuthoringRequestHash,
            operation,
            intent.GraphValidationEvidenceHash);
    }

    private static GovernedLoopGraphRevisionStoredOperation PendingOperation(
        GovernedLoopGraphRevisionIntentDocument intent)
        => new(
            GovernedLoopGraphRevisionOperationState.Pending,
            intent.GraphId,
            intent.OperationId,
            intent.LifecycleRequestHash,
            intent.AuthoringRequestHash,
            null,
            intent.GraphValidationEvidenceHash);

    private async Task<GovernedLoopGraphRevisionArtifactDocument> SignArtifactAsync(
        GovernedLoopGraphDefinition graph,
        string workspaceIdentity,
        long trustGeneration,
        CancellationToken cancellationToken)
    {
        var unsigned = new GovernedLoopGraphRevisionArtifactDocument(
            graph,
            GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graph),
            GovernedLoopGraphRevisionStoreJson.ComputePayloadHash(graph),
            workspaceIdentity,
            trustGeneration,
            string.Empty,
            string.Empty);
        var digest = GovernedLoopGraphRevisionStoreJson.ComputeContentDigest(unsigned);
        var tag = await _trustProvider.AuthenticateArtifactAsync(
            workspaceIdentity,
            trustGeneration,
            digest,
            cancellationToken);
        RequireAuthenticationTag(tag);
        return unsigned with { ContentDigest = digest, AuthenticationTag = tag };
    }

    private async Task<GovernedLoopGraphRevisionIntentDocument> SignIntentAsync(
        GovernedLoopGraphRevisionStoreMutation mutation,
        string? graphPayloadHash,
        string workspaceIdentity,
        long trustGeneration,
        CancellationToken cancellationToken)
    {
        var lifecycle = mutation.LifecycleMutation;
        var unsigned = new GovernedLoopGraphRevisionIntentDocument(
            GovernedLoopGraphRevisionIntentDocument.CurrentSchemaVersion,
            workspaceIdentity,
            trustGeneration,
            lifecycle.GraphId,
            lifecycle.Operation.OperationId,
            lifecycle.Operation.RequestHash,
            mutation.AuthoringRequestHash,
            graphPayloadHash,
            mutation.GraphValidationEvidenceHash,
            string.Empty,
            string.Empty);
        var digest = GovernedLoopGraphRevisionStoreJson.ComputeContentDigest(unsigned);
        var tag = await _trustProvider.AuthenticateArtifactAsync(
            workspaceIdentity,
            trustGeneration,
            digest,
            cancellationToken);
        RequireAuthenticationTag(tag);
        return unsigned with { ContentDigest = digest, AuthenticationTag = tag };
    }

    private async Task RequireTrustedAsync(
        GovernedLoopGraphRevisionArtifactDocument document,
        string workspaceIdentity,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
            || !await _trustProvider.VerifyArtifactAsync(
                workspaceIdentity,
                document.TrustGeneration,
                document.ContentDigest,
                document.AuthenticationTag,
                cancellationToken))
        {
            throw new FormatException("The governed-loop graph-revision artifact is unauthenticated or belongs to another physical workspace.");
        }
    }

    private async Task RequireTrustedAsync(
        GovernedLoopGraphRevisionIntentDocument document,
        string workspaceIdentity,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || Encoding.UTF8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
            || !await _trustProvider.VerifyArtifactAsync(
                workspaceIdentity,
                document.TrustGeneration,
                document.ContentDigest,
                document.AuthenticationTag,
                cancellationToken))
        {
            throw new FormatException("The governed-loop graph-authoring intent is unauthenticated or belongs to another physical workspace.");
        }
    }

    private async Task<CapabilityCatalogTrustState> ReadOrInitializeTrustAsync(
        string workspaceIdentity,
        CancellationToken cancellationToken)
    {
        return await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken)
            ?? await _trustProvider.InitializeAsync(
                workspaceIdentity,
                0,
                _emptyTrustDigest,
                cancellationToken);
    }

    private async Task ReconcileIntentTrustAsync(
        GovernedLoopGraphRevisionIntentDocument intent,
        CancellationToken cancellationToken)
    {
        var current = await ReadOrInitializeTrustAsync(intent.WorkspaceIdentity, cancellationToken);
        if (current.CurrentGeneration == intent.TrustGeneration)
        {
            if (!string.Equals(current.CurrentContentDigest, intent.ContentDigest, StringComparison.Ordinal))
            {
                throw new IOException("The graph-authoring trust generation is bound to different immutable intent.");
            }
            return;
        }

        if (current.CurrentGeneration == intent.TrustGeneration - 1)
        {
            var advanced = await _trustProvider.AdvanceAsync(
                intent.WorkspaceIdentity,
                current.CurrentGeneration,
                current.CurrentContentDigest,
                intent.TrustGeneration,
                intent.ContentDigest,
                cancellationToken);
            RequireExactTrustAdvance(advanced, intent.WorkspaceIdentity, current, intent.TrustGeneration, intent.ContentDigest);
            await ObserveAsync(GovernedLoopGraphRevisionPersistenceBoundary.TrustAdvanced, cancellationToken);
            return;
        }

        if (current.CurrentGeneration < intent.TrustGeneration)
        {
            throw new IOException("The graph-authoring trust anchor cannot skip generations during recovery.");
        }
    }

    private async Task ReconcilePendingTrustAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        IReadOnlyList<string> intentIds,
        CancellationToken cancellationToken)
    {
        var intents = new List<GovernedLoopGraphRevisionIntentDocument>(intentIds.Count);
        foreach (var intentId in intentIds)
        {
            intents.Add(await LoadIntentAsync(session, intentId, cancellationToken)
                ?? throw new FormatException("A discovered graph-authoring intent disappeared during trust reconciliation."));
        }

        var current = await ReadOrInitializeTrustAsync(workspaceIdentity, cancellationToken);
        var currentDigests = intents
            .Where(intent => intent.TrustGeneration == current.CurrentGeneration)
            .Select(intent => intent.ContentDigest)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if ((current.CurrentGeneration > 0 && currentDigests.Length != 1)
            || (currentDigests.Length == 1
                && !string.Equals(currentDigests[0], current.CurrentContentDigest, StringComparison.Ordinal)))
        {
            throw new IOException("The graph-authoring trust anchor is not bound to its immutable current-generation intent.");
        }

        for (var index = 0; index < intents.Count; index++)
        {
            var direct = intents
                .Where(intent => intent.TrustGeneration == current.CurrentGeneration + 1)
                .ToArray();
            if (direct.Length == 0)
            {
                break;
            }

            var digests = direct.Select(intent => intent.ContentDigest).Distinct(StringComparer.Ordinal).ToArray();
            if (digests.Length != 1)
            {
                throw new IOException("More than one immutable graph-authoring intent claims the same direct-successor trust generation.");
            }

            var next = direct[0];
            var advanced = await _trustProvider.AdvanceAsync(
                workspaceIdentity,
                current.CurrentGeneration,
                current.CurrentContentDigest,
                next.TrustGeneration,
                next.ContentDigest,
                cancellationToken);
            RequireExactTrustAdvance(advanced, workspaceIdentity, current, next.TrustGeneration, next.ContentDigest);
            await ObserveAsync(GovernedLoopGraphRevisionPersistenceBoundary.TrustAdvanced, cancellationToken);
            current = advanced;
        }

        if (intents.Any(intent => intent.TrustGeneration > current.CurrentGeneration + 1))
        {
            throw new IOException("Graph-authoring trust recovery refuses a non-contiguous future intent generation.");
        }
    }

    private async Task RequireReadableTrustPostureAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        IReadOnlyList<string> intentIds,
        CancellationToken cancellationToken)
    {
        var current = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken)
            ?? throw new IOException("The graph-authoring workspace has no server-owned trust anchor.");
        var intents = new List<GovernedLoopGraphRevisionIntentDocument>(intentIds.Count);
        foreach (var intentId in intentIds)
        {
            intents.Add(await LoadIntentAsync(session, intentId, cancellationToken)
                ?? throw new FormatException("A discovered graph-authoring intent disappeared during trust validation."));
        }

        var currentDigests = intents
            .Where(intent => intent.TrustGeneration == current.CurrentGeneration)
            .Select(intent => intent.ContentDigest)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if ((current.CurrentGeneration > 0 && currentDigests.Length != 1)
            || (currentDigests.Length == 1
                && !string.Equals(currentDigests[0], current.CurrentContentDigest, StringComparison.Ordinal)))
        {
            throw new IOException("The graph-authoring trust anchor is not bound to its immutable current-generation intent.");
        }

        var directSuccessors = intents
            .Where(intent => intent.TrustGeneration == current.CurrentGeneration + 1)
            .Select(intent => intent.ContentDigest)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (directSuccessors.Length > 1
            || intents.Any(intent => intent.TrustGeneration > current.CurrentGeneration + 1))
        {
            throw new IOException("The graph-authoring workspace contains competing or non-contiguous future trust evidence.");
        }
    }

    private async Task RequireDiscoveredArtifactsAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        IReadOnlyList<ArtifactIdentity> artifactIdentities,
        CancellationToken cancellationToken)
    {
        foreach (var identity in artifactIdentities)
        {
            var bytes = await session.TryReadAllBytesBoundAsync(
                _paths.ArtifactPath(identity.GraphId, identity.RevisionId),
                _options.MaxArtifactUtf8Bytes,
                cancellationToken) ?? throw new FormatException("A discovered immutable graph payload disappeared during validation.");
            var document = GovernedLoopGraphRevisionStoreJson.DeserializeArtifact(bytes);
            await RequireTrustedAsync(document, workspaceIdentity, cancellationToken);
            if (!string.Equals(document.Graph.GraphId, identity.GraphId, StringComparison.Ordinal)
                || !string.Equals(document.Graph.RevisionId, identity.RevisionId, StringComparison.Ordinal))
            {
                throw new FormatException("An immutable graph payload path does not match its graph and revision identity.");
            }
        }
    }

    private StoreShape InspectStore(CapabilityCatalogPathSession session)
    {
        var rootEntries = session.EnumerateBoundEntries(_paths.RootPath, 3);
        foreach (var entry in rootEntries)
        {
            var valid = entry.Name switch
            {
                ".mutations.lock" => entry.Kind == CapabilityCatalogDirectoryEntryKind.RegularFile,
                "artifacts" or "operations" => entry.Kind == CapabilityCatalogDirectoryEntryKind.Directory,
                _ => false,
            };
            if (!valid)
            {
                throw new FormatException("The graph-authoring root contains an unexpected, linked, or malformed entry.");
            }
        }

        if (!session.TryEnumerateStrictDirectories(_paths.ArtifactsPath, _options.MaxGraphDirectories, out var graphDirectories))
        {
            throw new IOException("The bounded graph-authoring graph-directory quota is exhausted.");
        }

        var artifactCount = 0;
        var artifactIdentities = new List<ArtifactIdentity>();
        var stagingEntries = new List<StagingEntry>();
        var stagingCount = 0;
        var totalBytes = 0L;
        foreach (var graphId in graphDirectories)
        {
            if (!IsMutableGraphId(graphId))
            {
                throw new FormatException("The graph-authoring artifact root contains a noncanonical or reserved graph identity.");
            }

            var files = session.EnumerateRegularFiles(
                Path.Combine(_paths.ArtifactsPath, graphId),
                checked(_options.MaxArtifacts + _options.MaxStagingEntries),
                _options.MaxWorkspaceUtf8Bytes - totalBytes);
            foreach (var file in files)
            {
                if (IsJsonIdentifier(file.Name, out var revisionId))
                {
                    if (file.Length is <= 0 || file.Length > _options.MaxArtifactUtf8Bytes)
                    {
                        throw new FormatException("The graph-authoring artifact root contains a malformed immutable artifact.");
                    }
                    artifactCount = checked(artifactCount + 1);
                    artifactIdentities.Add(new ArtifactIdentity(graphId, revisionId));
                }
                else if (TryParseStagingName(file.Name, out var staging))
                {
                    if (file.Length < 0 || file.Length > _options.MaxArtifactUtf8Bytes)
                    {
                        throw new FormatException("The graph-authoring artifact root contains an oversized staging entry.");
                    }
                    stagingCount = checked(stagingCount + 1);
                    stagingEntries.Add(new StagingEntry(
                        Path.Combine(_paths.ArtifactsPath, graphId, staging.DestinationName),
                        Path.Combine(_paths.ArtifactsPath, graphId, file.Name),
                        staging.Token,
                        file.Length,
                        staging.IsReady));
                }
                else
                {
                    throw new FormatException("The graph-authoring artifact root contains a malformed immutable artifact.");
                }
                totalBytes = checked(totalBytes + file.Length);
            }
        }

        var intents = session.EnumerateRegularFiles(
            _paths.OperationsPath,
            checked(_options.MaxIntents + _options.MaxStagingEntries),
            _options.MaxWorkspaceUtf8Bytes - totalBytes);
        var intentIds = new List<string>();
        var intentCount = 0;
        foreach (var intent in intents)
        {
            if (IsJsonIdentifier(intent.Name, out var intentId))
            {
                if (intent.Length is <= 0 || intent.Length > _options.MaxIntentUtf8Bytes)
                {
                    throw new FormatException("The graph-authoring operation root contains a malformed immutable intent.");
                }
                intentCount = checked(intentCount + 1);
                intentIds.Add(intentId);
            }
            else if (TryParseStagingName(intent.Name, out var staging))
            {
                if (intent.Length < 0 || intent.Length > _options.MaxIntentUtf8Bytes)
                {
                    throw new FormatException("The graph-authoring operation root contains an oversized staging entry.");
                }
                stagingCount = checked(stagingCount + 1);
                stagingEntries.Add(new StagingEntry(
                    Path.Combine(_paths.OperationsPath, staging.DestinationName),
                    Path.Combine(_paths.OperationsPath, intent.Name),
                    staging.Token,
                    intent.Length,
                    staging.IsReady));
            }
            else
            {
                throw new FormatException("The graph-authoring operation root contains a malformed immutable intent.");
            }
            totalBytes = checked(totalBytes + intent.Length);
        }

        if (artifactCount > _options.MaxArtifacts
            || intentCount > _options.MaxIntents
            || stagingCount > _options.MaxStagingEntries
            || totalBytes > _options.MaxWorkspaceUtf8Bytes)
        {
            throw new IOException("The bounded graph-authoring workspace quota is exhausted.");
        }
        return new StoreShape(
            artifactCount,
            intentCount,
            graphDirectories.Count,
            stagingCount,
            totalBytes,
            Array.AsReadOnly(artifactIdentities
                .OrderBy(identity => identity.GraphId, StringComparer.Ordinal)
                .ThenBy(identity => identity.RevisionId, StringComparer.Ordinal)
                .ToArray()),
            Array.AsReadOnly(intentIds.Order(StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(stagingEntries
                .OrderBy(entry => entry.StagingPath, StringComparer.Ordinal)
                .ToArray()));
    }

    private async Task RequireCapacityAsync(
        CapabilityCatalogPathSession session,
        StoreShape shape,
        bool reservesArtifact,
        bool reservesIntent,
        bool reservesGraphDirectory,
        IReadOnlyList<ImmutableWritePlan> plannedWrites,
        CancellationToken cancellationToken)
    {
        var stagingCount = shape.StagingCount;
        var totalBytes = shape.TotalBytes;
        foreach (var write in plannedWrites)
        {
            var digest = Convert.ToHexString(SHA256.HashData(write.Content)).ToLowerInvariant();
            var ready = shape.StagingEntries.SingleOrDefault(entry =>
                entry.IsReady
                && PathEquals(entry.DestinationPath, write.DestinationPath)
                && string.Equals(entry.Token, digest, StringComparison.Ordinal)
                && entry.Length == write.Content.LongLength);
            if (ready is not null)
            {
                var bytes = await session.TryReadAllBytesBoundAsync(
                    ready.StagingPath,
                    write.Content.Length,
                    cancellationToken);
                if (bytes is null || !CryptographicOperations.FixedTimeEquals(bytes, write.Content))
                {
                    throw new IOException("The exact immutable-write ready stage failed canonical byte verification.");
                }

                stagingCount = checked(stagingCount - 1);
                continue;
            }

            if (stagingCount >= _options.MaxStagingEntries)
            {
                throw new IOException("The immutable graph-authoring staging-entry quota is exhausted.");
            }
            totalBytes = checked(totalBytes + write.Content.LongLength);
        }

        if (shape.ArtifactCount + (reservesArtifact ? 1 : 0) > _options.MaxArtifacts
            || shape.IntentCount + (reservesIntent ? 1 : 0) > _options.MaxIntents
            || shape.GraphDirectoryCount + (reservesGraphDirectory ? 1 : 0) > _options.MaxGraphDirectories
            || totalBytes > _options.MaxWorkspaceUtf8Bytes)
        {
            throw new IOException("The immutable graph-authoring count or byte quota is exhausted.");
        }
    }

    private static GovernedLoopGraphDefinition? ValidateMutation(
        GovernedLoopGraphRevisionStoreMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(mutation.LifecycleMutation);
        ArgumentNullException.ThrowIfNull(mutation.LifecycleMutation.Operation);
        var lifecycle = mutation.LifecycleMutation;
        if (!IsMutableGraphId(lifecycle.GraphId)
            || !IsIdentifier(lifecycle.Operation.OperationId)
            || !IsHash(lifecycle.Operation.RequestHash)
            || !IsHash(mutation.AuthoringRequestHash)
            || mutation.GraphValidationEvidenceHash is not null && !IsHash(mutation.GraphValidationEvidenceHash)
            || (lifecycle.ArtifactToAppend is null) != (mutation.GraphToAppend is null))
        {
            throw new ArgumentException("The graph-authoring store mutation is invalid.", nameof(mutation));
        }

        if (mutation.GraphToAppend is null)
        {
            return null;
        }

        if (!string.Equals(mutation.GraphToAppend.GraphId, lifecycle.GraphId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The graph payload and lifecycle mutation identify different graphs.", nameof(mutation));
        }

        _ = GovernedLoopGraphRevisionArtifactFactory.Create(
            GovernedLoopGraphRevisionArtifact.CurrentSchemaVersion,
            lifecycle.ArtifactToAppend!,
            mutation.GraphToAppend);
        return mutation.GraphToAppend;
    }

    private static bool IntentMatches(
        GovernedLoopGraphRevisionIntentDocument intent,
        GovernedLoopGraphRevisionStoreMutation mutation,
        string? graphPayloadHash)
    {
        var lifecycle = mutation.LifecycleMutation;
        return string.Equals(intent.GraphId, lifecycle.GraphId, StringComparison.Ordinal)
            && string.Equals(intent.OperationId, lifecycle.Operation.OperationId, StringComparison.Ordinal)
            && string.Equals(intent.LifecycleRequestHash, lifecycle.Operation.RequestHash, StringComparison.Ordinal)
            && string.Equals(intent.AuthoringRequestHash, mutation.AuthoringRequestHash, StringComparison.Ordinal)
            && string.Equals(intent.GraphPayloadHash, graphPayloadHash, StringComparison.Ordinal)
            && string.Equals(intent.GraphValidationEvidenceHash, mutation.GraphValidationEvidenceHash, StringComparison.Ordinal);
    }

    private static bool IsValidRevision(GovernedLoopRevisionReference? revision)
    {
        if (revision is null)
        {
            return false;
        }
        try
        {
            _ = GovernedLoopRevisionReference.Create(
                revision.SchemaVersion,
                revision.GraphId,
                revision.RevisionId,
                revision.ExecutableHash);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool SameReference(
        GovernedLoopRevisionReference left,
        GovernedLoopRevisionReference right)
        => left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);

    private async Task<TResult> ReadUnderAuthorityAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        Func<GovernedLoopRevisionStoreReadStatus, TResult> unavailable,
        CancellationToken cancellationToken)
        where TResult : class
    {
        var callbackEntered = false;
        TResult? callbackResult = default;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await operation(token);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (callbackResult is not null)
            {
                return callbackResult;
            }
            if (!callbackEntered && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            return unavailable(GovernedLoopRevisionStoreReadStatus.Unavailable);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult ?? unavailable(GovernedLoopRevisionStoreReadStatus.Unavailable);
        }
    }

    private async Task<CapabilityCatalogPathSession> AcquireAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.LockPath, createRoot: true, cancellationToken)
            ?? throw new IOException("The governed-loop graph-authoring workspace root is unavailable.");

    private Task<CapabilityCatalogPathSession?> AcquireExistingAsync(CancellationToken cancellationToken)
        => _pathGuard.TryAcquireExclusiveSessionAsync(
            _paths.LockPath,
            createRoot: false,
            cancellationToken,
            createLockParent: false);

    private static string WorkspaceIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(
            "embodysense-governed-loop-graph-authoring-v1\n" + session.PhysicalIdentityMaterial);

    private static GovernedLoopGraphRevisionReadResult GraphRead(GovernedLoopRevisionStoreReadStatus status)
        => new(status, 0, null);

    private static GovernedLoopGraphRevisionArtifactReadResult ArtifactRead(GovernedLoopRevisionStoreReadStatus status)
        => new(status, 0, null);

    private static GovernedLoopGraphRevisionMutationReadResult MutationRead(GovernedLoopRevisionStoreReadStatus status)
        => new(status, 0, null, null);

    private static GovernedLoopGraphRevisionCommitResult Commit(GovernedLoopRevisionStoreCommitStatus status)
        => new(status, 0, null, null);

    private static GovernedLoopGraphRevisionStoreOptions ValidateOptions(GovernedLoopGraphRevisionStoreOptions options)
    {
        if (options.MaxArtifactUtf8Bytes is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumArtifactUtf8Bytes
            || options.MaxIntentUtf8Bytes is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumIntentUtf8Bytes
            || options.MaxWorkspaceUtf8Bytes is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumWorkspaceUtf8Bytes
            || options.MaxArtifacts is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumArtifacts
            || options.MaxIntents is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumIntents
            || options.MaxGraphDirectories is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumGraphDirectories
            || options.MaxStagingEntries is < 1 or > GovernedLoopGraphRevisionStoreOptions.MaximumStagingEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Graph-authoring persistence options must remain inside schema-1 bounds.");
        }
        return options;
    }

    private void RequireArtifactSize(byte[] bytes)
    {
        if (bytes.Length is <= 0 || bytes.Length > _options.MaxArtifactUtf8Bytes)
        {
            throw new IOException("The immutable graph-revision artifact exceeds its configured UTF-8 bound.");
        }
    }

    private void RequireIntentSize(byte[] bytes)
    {
        if (bytes.Length is <= 0 || bytes.Length > _options.MaxIntentUtf8Bytes)
        {
            throw new IOException("The immutable graph-authoring intent exceeds its configured UTF-8 bound.");
        }
    }

    private void RequireAuthenticationTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag)
            || Encoding.UTF8.GetByteCount(tag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The graph-authoring trust provider returned an authentication tag outside its declared bound.");
        }
    }

    private static void RequireExactTrustAdvance(
        CapabilityCatalogTrustState advanced,
        string workspaceIdentity,
        CapabilityCatalogTrustState previous,
        long generation,
        string contentDigest)
    {
        if (!string.Equals(advanced.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || advanced.CurrentGeneration != generation
            || !string.Equals(advanced.CurrentContentDigest, contentDigest, StringComparison.Ordinal)
            || advanced.PreviousGeneration != previous.CurrentGeneration
            || !string.Equals(advanced.PreviousContentDigest, previous.CurrentContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The server-owned graph-authoring trust provider did not return the exact direct successor.");
        }
    }

    private ValueTask ObserveAsync(
        GovernedLoopGraphRevisionPersistenceBoundary boundary,
        CancellationToken cancellationToken)
        => _options.DurableBoundaryObserver is { } observer
            ? observer(boundary, cancellationToken)
            : ValueTask.CompletedTask;

    private static bool IsMutableGraphId(string? value)
        => IsIdentifier(value)
            && !string.Equals(value, BuiltInLoopIds.DefaultConversation, StringComparison.Ordinal);

    private static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters);

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopRevisionContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsJsonIdentifier(string value, out string identifier)
    {
        identifier = string.Empty;
        if (!value.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }
        identifier = value[..^5];
        return IsIdentifier(identifier);
    }

    private static bool TryParseStagingName(string value, out ParsedStagingName staging)
    {
        staging = null!;
        if (value.Length == 0 || value[0] != '.')
        {
            return false;
        }

        var suffixLength = value.EndsWith(".writing", StringComparison.Ordinal)
            ? ".writing".Length
            : value.EndsWith(".ready", StringComparison.Ordinal)
                ? ".ready".Length
                : 0;
        var tokenLength = suffixLength == ".writing".Length ? 32 : 64;
        if (suffixLength == 0 || value.Length <= suffixLength + tokenLength + 2)
        {
            return false;
        }

        var body = value[1..^suffixLength];
        var separator = body.LastIndexOf('.');
        if (separator <= 0)
        {
            return false;
        }

        var destination = body[..separator];
        var token = body[(separator + 1)..];
        if (!IsJsonIdentifier(destination, out _)
            || token.Length != tokenLength
            || !token.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        staging = new ParsedStagingName(
            destination,
            token,
            suffixLength == ".ready".Length);
        return true;
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or FormatException
            or System.Text.Json.JsonException
            or CryptographicException
            or OverflowException
            or ArgumentException;

    private sealed record StoreShape(
        int ArtifactCount,
        int IntentCount,
        int GraphDirectoryCount,
        int StagingCount,
        long TotalBytes,
        IReadOnlyList<ArtifactIdentity> ArtifactIdentities,
        IReadOnlyList<string> IntentIds,
        IReadOnlyList<StagingEntry> StagingEntries);

    private sealed record ArtifactIdentity(string GraphId, string RevisionId);

    private sealed record ParsedStagingName(string DestinationName, string Token, bool IsReady);

    private sealed record StagingEntry(
        string DestinationPath,
        string StagingPath,
        string Token,
        long Length,
        bool IsReady);

    private sealed record ImmutableWritePlan(string DestinationPath, byte[] Content);
}
