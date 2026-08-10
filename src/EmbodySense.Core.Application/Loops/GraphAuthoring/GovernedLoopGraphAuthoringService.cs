using System.Security.Cryptography;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Application.Loops.GraphValidation;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.GraphAuthoring;

/// <summary>Composes canonical graph payload authoring over the shared immutable revision lifecycle.</summary>
/// <remarks>The service owns graph-payload composition and full-intent idempotency only. Generic lifecycle planning, actor authorization, publication posture, and optimistic retry remain owned by <see cref="GovernedLoopRevisionLifecycleService"/>.</remarks>
public sealed class GovernedLoopGraphAuthoringService : IGovernedLoopGraphAuthoringService
{
    private readonly IGovernedLoopGraphRevisionStore _store;
    private readonly GovernedLoopGraphValidationService _graphValidationService;
    private readonly IGovernedLoopRevisionActorAuthorizer _authorizer;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a graph authoring service over server-owned graph, lifecycle, and authority ports.</summary>
    public GovernedLoopGraphAuthoringService(
        IGovernedLoopGraphRevisionStore store,
        GovernedLoopGraphValidationService graphValidationService,
        IGovernedLoopRevisionActorAuthorizer authorizer,
        ICapabilityAuthorityTransaction authorityTransaction,
        TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _graphValidationService = graphValidationService ?? throw new ArgumentNullException(nameof(graphValidationService));
        _authorizer = authorizer ?? throw new ArgumentNullException(nameof(authorizer));
        _authorityTransaction = authorityTransaction ?? throw new ArgumentNullException(nameof(authorityTransaction));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<GovernedLoopGraphAuthoringResult> MutateAsync(
        GovernedLoopGraphAuthoringRequest? request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                token => MutateUnderFenceAsync(request, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Result(
                GovernedLoopGraphAuthoringStatus.Unavailable,
                SafeOperationId(request),
                string.Empty);
        }
    }

    private async Task<GovernedLoopGraphAuthoringResult> MutateUnderFenceAsync(
        GovernedLoopGraphAuthoringRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null || request.LifecycleRequest is null)
        {
            return Result(GovernedLoopGraphAuthoringStatus.Invalid, SafeOperationId(request), string.Empty);
        }

        var lifecycle = request.LifecycleRequest;
        var lifecycleErrors = GovernedLoopRevisionLifecycleRequestValidator.Validate(lifecycle);
        if (request.SchemaVersion != GovernedLoopGraphDefinition.CurrentSchemaVersion
            || lifecycleErrors.Count > 0)
        {
            return Result(
                GovernedLoopGraphAuthoringStatus.Invalid,
                lifecycle.OperationId ?? string.Empty,
                string.Empty,
                lifecycleErrors: lifecycleErrors);
        }

        if (string.Equals(lifecycle.GraphId, BuiltInLoopIds.DefaultConversation, StringComparison.Ordinal))
        {
            return Result(
                GovernedLoopGraphAuthoringStatus.Invalid,
                lifecycle.OperationId,
                string.Empty,
                graphErrors: OneGraphError(
                    "graph.system-read-only",
                    lifecycle.GraphId,
                    "graph.graphId",
                    "Built-in system-loop graphs are immutable and cannot enter custom graph authoring."));
        }

        var requiresCandidate = lifecycle.Kind is GovernedLoopRevisionOperationKind.CreateDraft
            or GovernedLoopRevisionOperationKind.ReplaceDraft;
        if (requiresCandidate != (request.GraphCandidate is not null))
        {
            return Result(
                GovernedLoopGraphAuthoringStatus.Invalid,
                lifecycle.OperationId,
                string.Empty,
                graphErrors: OneGraphError(
                    requiresCandidate ? "graph.candidate.required" : "graph.candidate.unexpected",
                    lifecycle.GraphId,
                    "graphCandidate",
                    requiresCandidate
                        ? "Create and replace operations require one graph candidate."
                        : "Only create and replace operations accept a caller-supplied graph candidate."));
        }

        GovernedLoopGraphDefinition? normalizedGraph = null;
        if (request.GraphCandidate is not null)
        {
            var normalized = GovernedLoopGraphNormalizer.Normalize(request.GraphCandidate);
            if (!normalized.IsValid)
            {
                return Result(
                    GovernedLoopGraphAuthoringStatus.Invalid,
                    lifecycle.OperationId,
                    string.Empty,
                    graphErrors: normalized.Errors);
            }

            normalizedGraph = normalized.Graph;
            if (!SameRevision(normalizedGraph!.RevisionReference, lifecycle.CandidateRevision))
            {
                return Result(
                    GovernedLoopGraphAuthoringStatus.Invalid,
                    lifecycle.OperationId,
                    string.Empty,
                    graphErrors: OneGraphError(
                        "graph.candidate-revision.mismatch",
                        normalizedGraph.GraphId,
                        "graphCandidate",
                        "The normalized graph must match the lifecycle candidate's exact graph, revision, and executable identity."));
            }
        }

        var graphRead = await ReadGraphAsync(lifecycle.GraphId, cancellationToken).ConfigureAwait(false);
        var graphReadFailure = MapGraphReadFailure(graphRead, lifecycle.OperationId);
        if (graphReadFailure is not null)
        {
            return graphReadFailure;
        }

        var preflightSnapshot = graphRead!.Snapshot;
        var rollbackSourceArtifact = FindArtifact(preflightSnapshot, lifecycle.RollbackSourcePublication?.Revision);
        GovernedLoopGraphDefinition? graphToAppend = normalizedGraph;
        if (lifecycle.Kind == GovernedLoopRevisionOperationKind.Rollback && rollbackSourceArtifact is not null)
        {
            var copy = GovernedLoopGraphNormalizer.Normalize(
                GovernedLoopGraphCandidateProjection.CopyAsRevision(
                    rollbackSourceArtifact.Graph,
                    lifecycle.CandidateRevision!));
            if (!copy.IsValid
                || !string.Equals(copy.Graph!.ExecutableHash, rollbackSourceArtifact.Graph.ExecutableHash, StringComparison.Ordinal))
            {
                return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, lifecycle.OperationId, string.Empty);
            }

            graphToAppend = copy.Graph;
        }

        string authoringRequestHash;
        string lifecycleRequestHash;
        try
        {
            authoringRequestHash = GovernedLoopGraphAuthoringRequestHash.Compute(
                lifecycle,
                normalizedGraph);
            lifecycleRequestHash = GovernedLoopRevisionLifecycleRequestHash.Compute(lifecycle);
        }
        catch (ArgumentException)
        {
            return Result(GovernedLoopGraphAuthoringStatus.Invalid, lifecycle.OperationId, string.Empty);
        }

        var initialRead = await ReadForMutationAsync(
            lifecycle.GraphId,
            lifecycle.OperationId,
            lifecycleRequestHash,
            authoringRequestHash,
            cancellationToken).ConfigureAwait(false);
        var readFailure = MapReadFailure(initialRead, lifecycle.OperationId, authoringRequestHash);
        if (readFailure is not null)
        {
            return readFailure;
        }

        if (!SnapshotsAgree(preflightSnapshot, initialRead!.Snapshot))
        {
            return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, lifecycle.OperationId, authoringRequestHash);
        }

        var resumeExactIntent = false;
        string? graphValidationEvidenceHash = null;
        if (initialRead.ExistingOperation is { } existing)
        {
            if (!TryValidateStoredOperation(existing, lifecycle.OperationId)
                || !string.Equals(existing.GraphId, lifecycle.GraphId, StringComparison.Ordinal))
            {
                return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, lifecycle.OperationId, authoringRequestHash);
            }

            if (!string.Equals(existing.AuthoringRequestHash, authoringRequestHash, StringComparison.Ordinal))
            {
                return Result(GovernedLoopGraphAuthoringStatus.Conflict, lifecycle.OperationId, authoringRequestHash);
            }

            if (existing.State == GovernedLoopGraphRevisionOperationState.Terminal
                && !string.Equals(existing.LifecycleOperation!.Evidence.RequestHash, lifecycleRequestHash, StringComparison.Ordinal))
            {
                return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, lifecycle.OperationId, authoringRequestHash);
            }

            resumeExactIntent = true;
            graphValidationEvidenceHash = existing.GraphValidationEvidenceHash;
        }

        if (normalizedGraph is not null && !resumeExactIntent)
        {
            GovernedLoopGraphValidationResult validation;
            try
            {
                validation = await _graphValidationService.ValidateAsync(
                    GovernedLoopGraphCandidateProjection.FromDefinition(normalizedGraph),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Result(GovernedLoopGraphAuthoringStatus.Unavailable, lifecycle.OperationId, authoringRequestHash);
            }

            if (validation is null || !validation.IsValid)
            {
                var unavailable = validation is null
                    || validation.Errors.Any(error => error.Code is "catalog.unavailable" or "authority.unavailable");
                return Result(
                    unavailable
                        ? GovernedLoopGraphAuthoringStatus.Unavailable
                        : GovernedLoopGraphAuthoringStatus.ValidationRejected,
                    lifecycle.OperationId,
                    authoringRequestHash,
                    graphErrors: validation?.Errors ?? Array.Empty<GovernedLoopGraphValidationError>());
            }

            graphToAppend = validation.Graph;
            graphValidationEvidenceHash = GovernedLoopGraphValidationBindingHash.Compute(
                authoringRequestHash,
                graphToAppend!,
                validation.Evidence!.CombinedHash);
        }

        var changeKind = ClassifyChange(lifecycle, initialRead.Snapshot, graphToAppend);
        var adapter = new GovernedLoopGraphRevisionLifecycleStoreAdapter(
            _store,
            lifecycle.GraphId,
            lifecycle.OperationId,
            authoringRequestHash,
            graphToAppend,
            graphValidationEvidenceHash);
        var publishValidator = new GovernedLoopGraphRevisionPublishValidator(
            _store,
            _graphValidationService,
            authoringRequestHash,
            lifecycle.Kind == GovernedLoopRevisionOperationKind.Rollback ? graphToAppend : null);
        var lifecycleService = new GovernedLoopRevisionLifecycleService(
            adapter,
            _authorizer,
            publishValidator,
            _authorityTransaction,
            _timeProvider);

        var lifecycleResult = await lifecycleService.MutateAsync(lifecycle, cancellationToken).ConfigureAwait(false);
        var status = MapLifecycleStatus(lifecycleResult.Status);
        var proof = await ResolveDurableProofAsync(
            lifecycle,
            lifecycleRequestHash,
            authoringRequestHash,
            graphToAppend,
            lifecycleResult,
            adapter.LastCommit,
            cancellationToken).ConfigureAwait(false);
        if (proof.StatusOverride is { } statusOverride)
        {
            return Result(statusOverride, lifecycle.OperationId, authoringRequestHash);
        }

        var identity = status is GovernedLoopGraphAuthoringStatus.Committed or GovernedLoopGraphAuthoringStatus.Replayed
            ? FindIdentity(proof.Snapshot, RevisionForResult(lifecycle))
            : null;
        var contentCommitted = lifecycleResult.Evidence?.Outcome == GovernedLoopRevisionOperationOutcome.Committed;
        if (contentCommitted
            && identity is null)
        {
            return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, lifecycle.OperationId, authoringRequestHash);
        }

        return Result(
            status,
            lifecycle.OperationId,
            authoringRequestHash,
            lifecycleResult,
            proof.Operation?.GraphValidationEvidenceHash,
            contentCommitted
                ? changeKind
                : GovernedLoopGraphRevisionChangeKind.Unknown,
            identity,
            lifecycleErrors: lifecycleResult.ValidationErrors);
    }

    private async Task<GovernedLoopGraphRevisionMutationReadResult?> ReadForMutationAsync(
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ReadForMutationAsync(
                graphId,
                operationId,
                lifecycleRequestHash,
                authoringRequestHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopGraphRevisionMutationReadResult(
                GovernedLoopRevisionStoreReadStatus.Unavailable,
                0,
                null,
                null);
        }
    }

    private async Task<GovernedLoopGraphRevisionReadResult?> ReadGraphAsync(
        string graphId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _store.ReadGraphAsync(graphId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new GovernedLoopGraphRevisionReadResult(
                GovernedLoopRevisionStoreReadStatus.Unavailable,
                0,
                null);
        }
    }

    private static GovernedLoopGraphAuthoringResult? MapGraphReadFailure(
        GovernedLoopGraphRevisionReadResult? read,
        string operationId)
    {
        if (read is null
            || !Enum.IsDefined(read.Status)
            || read.StoreGeneration < 0)
        {
            return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, string.Empty);
        }

        return read.Status switch
        {
            GovernedLoopRevisionStoreReadStatus.Ready when read.StoreGeneration > 0
                && TryValidateSnapshot(read.Snapshot, read.StoreGeneration) => null,
            GovernedLoopRevisionStoreReadStatus.NotFound when read.Snapshot is null => null,
            GovernedLoopRevisionStoreReadStatus.Unavailable when read.StoreGeneration == 0 && read.Snapshot is null
                => Result(GovernedLoopGraphAuthoringStatus.Unavailable, operationId, string.Empty),
            _ => Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, string.Empty),
        };
    }

    private async Task<DurableProof> ResolveDurableProofAsync(
        GovernedLoopRevisionLifecycleRequest lifecycle,
        string lifecycleRequestHash,
        string authoringRequestHash,
        GovernedLoopGraphDefinition? graphToAppend,
        GovernedLoopRevisionLifecycleMutationResult lifecycleResult,
        GovernedLoopGraphRevisionCommitResult? commit,
        CancellationToken cancellationToken)
    {
        if (lifecycleResult.Evidence is null)
        {
            return DurableProof.None;
        }

        var read = await ReadForMutationAsync(
            lifecycle.GraphId,
            lifecycle.OperationId,
            lifecycleRequestHash,
            authoringRequestHash,
            cancellationToken).ConfigureAwait(false);
        if (MapReadFailure(read, lifecycle.OperationId, authoringRequestHash) is not null
            || read?.ExistingOperation is not { } operation
            || !TerminalOperationMatches(
                operation,
                lifecycle.GraphId,
                lifecycle.OperationId,
                lifecycleRequestHash,
                authoringRequestHash,
                lifecycleResult.Evidence)
            || !SnapshotProvesOperation(read.Snapshot, operation.LifecycleOperation!.Evidence))
        {
            return DurableProof.Ambiguous;
        }

        if (commit is not null
            && (!Enum.IsDefined(commit.Status)
                || commit.Status is not (GovernedLoopRevisionStoreCommitStatus.Committed or GovernedLoopRevisionStoreCommitStatus.Replayed)
                || commit.StoreGeneration <= 0
                || commit.Operation is null
                || !TerminalOperationMatches(
                    commit.Operation,
                    lifecycle.GraphId,
                    lifecycle.OperationId,
                    lifecycleRequestHash,
                    authoringRequestHash,
                    lifecycleResult.Evidence)
                || !SnapshotsAgree(commit.Snapshot, read.Snapshot)))
        {
            return DurableProof.Ambiguous;
        }

        if (lifecycleResult.Evidence.Outcome == GovernedLoopRevisionOperationOutcome.Committed)
        {
            var artifact = FindArtifact(read.Snapshot, RevisionForResult(lifecycle));
            if (artifact is null
                || lifecycleResult.Head is null
                || !Equals(read.Snapshot?.Lifecycle.Head, lifecycleResult.Head)
                || graphToAppend is not null
                    && (!SameRevision(artifact.RevisionArtifact.Revision, graphToAppend.RevisionReference)
                        || !string.Equals(artifact.Graph.ExecutableHash, graphToAppend.ExecutableHash, StringComparison.Ordinal)
                        || !string.Equals(
                            artifact.LayoutHash,
                            GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graphToAppend),
                            StringComparison.Ordinal)))
            {
                return DurableProof.Ambiguous;
            }
        }

        return new DurableProof(null, operation, read.Snapshot);
    }

    private static bool SnapshotProvesOperation(
        GovernedLoopGraphRevisionSnapshot? snapshot,
        GovernedLoopRevisionOperationEvidence evidence)
    {
        if (evidence.ResultHead is null)
        {
            return snapshot is null;
        }

        return snapshot is not null
            && Equals(snapshot.Lifecycle.Head, evidence.ResultHead)
            && snapshot.Lifecycle.Operations.Any(candidate => Equals(candidate, evidence));
    }

    private static bool TerminalOperationMatches(
        GovernedLoopGraphRevisionStoredOperation operation,
        string graphId,
        string operationId,
        string lifecycleRequestHash,
        string authoringRequestHash,
        GovernedLoopRevisionOperationEvidence evidence)
    {
        return TryValidateStoredOperation(operation, operationId)
            && operation.State == GovernedLoopGraphRevisionOperationState.Terminal
            && string.Equals(operation.GraphId, graphId, StringComparison.Ordinal)
            && string.Equals(operation.LifecycleRequestHash, lifecycleRequestHash, StringComparison.Ordinal)
            && string.Equals(operation.AuthoringRequestHash, authoringRequestHash, StringComparison.Ordinal)
            && Equals(operation.LifecycleOperation!.Evidence, evidence);
    }

    private static bool SnapshotsAgree(
        GovernedLoopGraphRevisionSnapshot? left,
        GovernedLoopGraphRevisionSnapshot? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return Equals(left.Lifecycle.Head, right.Lifecycle.Head)
            && left.Lifecycle.Artifacts.SequenceEqual(right.Lifecycle.Artifacts)
            && left.Lifecycle.Operations.SequenceEqual(right.Lifecycle.Operations)
            && left.Artifacts.Select(artifact => artifact.ArtifactHash)
                .SequenceEqual(right.Artifacts.Select(artifact => artifact.ArtifactHash), StringComparer.Ordinal);
    }

    private static GovernedLoopGraphAuthoringResult? MapReadFailure(
        GovernedLoopGraphRevisionMutationReadResult? read,
        string operationId,
        string requestHash)
    {
        if (read is null
            || !Enum.IsDefined(read.Status)
            || read.StoreGeneration < 0)
        {
            return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, requestHash);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.Unavailable)
        {
            return read.StoreGeneration == 0 && read.Snapshot is null && read.ExistingOperation is null
                ? Result(GovernedLoopGraphAuthoringStatus.Unavailable, operationId, requestHash)
                : Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, requestHash);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.Ambiguous)
        {
            return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, requestHash);
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.Ready)
        {
            return read.StoreGeneration <= 0
                || !TryValidateSnapshot(read.Snapshot, read.StoreGeneration)
                    ? Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, requestHash)
                    : null;
        }

        if (read.Status == GovernedLoopRevisionStoreReadStatus.NotFound)
        {
            return read.Snapshot is not null
                ? Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, requestHash)
                : null;
        }

        return Result(GovernedLoopGraphAuthoringStatus.Ambiguous, operationId, requestHash);
    }

    private static bool TryValidateSnapshot(
        GovernedLoopGraphRevisionSnapshot? snapshot,
        long storeGeneration)
    {
        if (snapshot?.Lifecycle is null
            || snapshot.Artifacts is null
            || snapshot.Lifecycle.Head is null
            || snapshot.Lifecycle.Artifacts is null
            || snapshot.Lifecycle.Operations is null
            || !GovernedLoopRevisionStoreSnapshotGuard.TryCaptureAtGeneration(
                snapshot.Lifecycle,
                snapshot.Lifecycle.Head.GraphId,
                storeGeneration,
                out var captured)
            || captured is null
            || snapshot.Artifacts.Count != captured.Artifacts.Count)
        {
            return false;
        }

        var revisionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in snapshot.Artifacts)
        {
            if (artifact is null
                || !revisionIds.Add(artifact.RevisionArtifact.Revision.RevisionId))
            {
                return false;
            }

            try
            {
                if (!string.Equals(
                        GovernedLoopGraphRevisionContractHash.ComputeArtifactHash(artifact),
                        artifact.ArtifactHash,
                        StringComparison.Ordinal)
                    || !captured.Artifacts.Any(candidate => Equals(candidate, artifact.RevisionArtifact)))
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateStoredOperation(
        GovernedLoopGraphRevisionStoredOperation operation,
        string expectedOperationId)
    {
        if (!Enum.IsDefined(operation.State)
            || operation.State == GovernedLoopGraphRevisionOperationState.Unknown
            || !string.Equals(operation.OperationId, expectedOperationId, StringComparison.Ordinal)
            || !CustomLoopArtifactIdentifier.IsValid(operation.GraphId)
            || !IsSha256(operation.LifecycleRequestHash)
            || !IsSha256(operation.AuthoringRequestHash)
            || operation.GraphValidationEvidenceHash is not null && !IsSha256(operation.GraphValidationEvidenceHash))
        {
            return false;
        }

        return operation.State switch
        {
            GovernedLoopGraphRevisionOperationState.Pending => operation.LifecycleOperation is null,
            GovernedLoopGraphRevisionOperationState.Terminal => operation.LifecycleOperation is not null
                && string.Equals(operation.LifecycleOperation.GraphId, operation.GraphId, StringComparison.Ordinal)
                && string.Equals(operation.LifecycleOperation.Evidence.OperationId, operation.OperationId, StringComparison.Ordinal)
                && string.Equals(operation.LifecycleOperation.Evidence.RequestHash, operation.LifecycleRequestHash, StringComparison.Ordinal)
                && GovernedLoopRevisionContractValidator.Validate(operation.LifecycleOperation.Evidence).IsValid,
            _ => false,
        };
    }

    private static GovernedLoopGraphRevisionArtifact? FindArtifact(
        GovernedLoopGraphRevisionSnapshot? snapshot,
        GovernedLoopRevisionReference? revision)
    {
        if (snapshot is null || revision is null)
        {
            return null;
        }

        return snapshot.Artifacts.SingleOrDefault(artifact => SameRevision(artifact.RevisionArtifact.Revision, revision));
    }

    private static GovernedLoopGraphRevisionIdentity? FindIdentity(
        GovernedLoopGraphRevisionSnapshot? snapshot,
        GovernedLoopRevisionReference? revision)
    {
        var artifact = FindArtifact(snapshot, revision);
        return artifact is null
            ? null
            : new GovernedLoopGraphRevisionIdentity(
                artifact.RevisionArtifact.Revision,
                artifact.LayoutHash,
                artifact.ArtifactHash);
    }

    private static GovernedLoopGraphRevisionChangeKind ClassifyChange(
        GovernedLoopRevisionLifecycleRequest lifecycle,
        GovernedLoopGraphRevisionSnapshot? snapshot,
        GovernedLoopGraphDefinition? graphToAppend)
    {
        if (lifecycle.Kind == GovernedLoopRevisionOperationKind.CreateDraft)
        {
            return GovernedLoopGraphRevisionChangeKind.Initial;
        }

        if (lifecycle.Kind == GovernedLoopRevisionOperationKind.Rollback)
        {
            return GovernedLoopGraphRevisionChangeKind.RollbackCopy;
        }

        if (lifecycle.Kind != GovernedLoopRevisionOperationKind.ReplaceDraft)
        {
            return GovernedLoopGraphRevisionChangeKind.LifecycleOnly;
        }

        var predecessor = FindArtifact(snapshot, lifecycle.TargetRevision);
        if (predecessor is null || graphToAppend is null)
        {
            return GovernedLoopGraphRevisionChangeKind.Unknown;
        }

        if (!string.Equals(predecessor.Graph.ExecutableHash, graphToAppend.ExecutableHash, StringComparison.Ordinal))
        {
            return GovernedLoopGraphRevisionChangeKind.Executable;
        }

        var layoutHash = GovernedLoopGraphRevisionContractHash.ComputeLayoutHash(graphToAppend);
        return string.Equals(predecessor.LayoutHash, layoutHash, StringComparison.Ordinal)
            ? GovernedLoopGraphRevisionChangeKind.IdentityOnly
            : GovernedLoopGraphRevisionChangeKind.LayoutOnly;
    }

    private static GovernedLoopRevisionReference? RevisionForResult(
        GovernedLoopRevisionLifecycleRequest lifecycle)
    {
        return lifecycle.CandidateRevision ?? lifecycle.TargetRevision;
    }

    private static GovernedLoopGraphAuthoringStatus MapLifecycleStatus(
        GovernedLoopRevisionLifecycleMutationStatus status)
    {
        return status switch
        {
            GovernedLoopRevisionLifecycleMutationStatus.Committed => GovernedLoopGraphAuthoringStatus.Committed,
            GovernedLoopRevisionLifecycleMutationStatus.Replayed => GovernedLoopGraphAuthoringStatus.Replayed,
            GovernedLoopRevisionLifecycleMutationStatus.Invalid => GovernedLoopGraphAuthoringStatus.Invalid,
            GovernedLoopRevisionLifecycleMutationStatus.Unauthorized => GovernedLoopGraphAuthoringStatus.Unauthorized,
            GovernedLoopRevisionLifecycleMutationStatus.Conflict => GovernedLoopGraphAuthoringStatus.Conflict,
            GovernedLoopRevisionLifecycleMutationStatus.NotFound => GovernedLoopGraphAuthoringStatus.NotFound,
            GovernedLoopRevisionLifecycleMutationStatus.LimitExceeded => GovernedLoopGraphAuthoringStatus.LimitExceeded,
            GovernedLoopRevisionLifecycleMutationStatus.PublicationRejected => GovernedLoopGraphAuthoringStatus.PublicationRejected,
            GovernedLoopRevisionLifecycleMutationStatus.Unavailable => GovernedLoopGraphAuthoringStatus.Unavailable,
            _ => GovernedLoopGraphAuthoringStatus.Ambiguous,
        };
    }

    private static GovernedLoopGraphAuthoringResult Result(
        GovernedLoopGraphAuthoringStatus status,
        string operationId,
        string requestHash,
        GovernedLoopRevisionLifecycleMutationResult? lifecycleResult = null,
        string? graphValidationEvidenceHash = null,
        GovernedLoopGraphRevisionChangeKind changeKind = GovernedLoopGraphRevisionChangeKind.Unknown,
        GovernedLoopGraphRevisionIdentity? revisionIdentity = null,
        IReadOnlyList<GovernedLoopGraphValidationError>? graphErrors = null,
        IReadOnlyList<GovernedLoopRevisionLifecycleValidationError>? lifecycleErrors = null)
    {
        return new GovernedLoopGraphAuthoringResult(
            status,
            operationId ?? string.Empty,
            requestHash,
            lifecycleResult,
            graphValidationEvidenceHash,
            changeKind,
            revisionIdentity,
            graphErrors ?? Array.Empty<GovernedLoopGraphValidationError>(),
            lifecycleErrors ?? Array.Empty<GovernedLoopRevisionLifecycleValidationError>());
    }

    private static IReadOnlyList<GovernedLoopGraphValidationError> OneGraphError(
        string code,
        string? id,
        string path,
        string message)
    {
        return Array.AsReadOnly(new[]
        {
            new GovernedLoopGraphValidationError(
                code,
                new GovernedLoopGraphElementReference(GovernedLoopGraphElementKind.Graph, id, path),
                message),
        });
    }

    private static bool SameRevision(
        GovernedLoopRevisionReference? left,
        GovernedLoopRevisionReference? right)
    {
        return left is not null
            && right is not null
            && left.SchemaVersion == right.SchemaVersion
            && string.Equals(left.GraphId, right.GraphId, StringComparison.Ordinal)
            && string.Equals(left.RevisionId, right.RevisionId, StringComparison.Ordinal)
            && string.Equals(left.ExecutableHash, right.ExecutableHash, StringComparison.Ordinal);
    }

    private static bool IsSha256(string? value)
    {
        return value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            && SHA256.HashSizeInBytes == 32;
    }

    private static string SafeOperationId(GovernedLoopGraphAuthoringRequest? request)
        => request?.LifecycleRequest?.OperationId ?? string.Empty;

    private sealed record DurableProof(
        GovernedLoopGraphAuthoringStatus? StatusOverride,
        GovernedLoopGraphRevisionStoredOperation? Operation,
        GovernedLoopGraphRevisionSnapshot? Snapshot)
    {
        internal static DurableProof None { get; } = new(null, null, null);
        internal static DurableProof Ambiguous { get; } = new(GovernedLoopGraphAuthoringStatus.Ambiguous, null, null);
    }
}
