using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops.Revisions;
using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.Revisions;
using EmbodySense.Core.Common.Loops.Revisions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Loops.Revisions.Models;

namespace EmbodySense.Core.Persistence.Loops.Revisions;

/// <summary>Persists one bounded, authenticated, append-only governed-loop revision lifecycle document.</summary>
/// <remarks>
/// Workspace artifacts are untrusted projections. A server-owned monotonic trust provider binds the exact current
/// document to the physical workspace, while the shared capability-authority transaction and a retained-handle path
/// session serialize authority-sensitive mutations. A signed direct successor left behind after primary publication is
/// finalized only by an exact graph, operation-id, and request-hash retry. Other callers receive an explicit ambiguous
/// outcome. Last-proved recovery state is read-only and never becomes a mutation base.
/// </remarks>
public sealed class GovernedLoopRevisionLifecycleStore : IGovernedLoopRevisionLifecycleStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly GovernedLoopRevisionStorePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly GovernedLoopRevisionStoreOptions _options;

    /// <summary>Creates a governed-loop revision store with the default server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="options">Optional bounded store and deterministic observer options.</param>
    /// <param name="durabilityBarrier">An optional trusted post-rename durability adapter.</param>
    /// <param name="authorityTransaction">An optional shared workspace capability-authority transaction.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paths" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="options" /> is outside schema-1 bounds.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the default trust root overlaps the governed workspace.</exception>
    public GovernedLoopRevisionLifecycleStore(
        WorkspacePaths paths,
        GovernedLoopRevisionStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), options, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates a governed-loop revision store over an explicit server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="trustProvider">The server-owned trust provider outside mutable workspace storage.</param>
    /// <param name="options">Optional bounded store and deterministic observer options.</param>
    /// <param name="durabilityBarrier">An optional trusted post-rename durability adapter.</param>
    /// <param name="authorityTransaction">An optional shared workspace capability-authority transaction.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="paths" /> or <paramref name="trustProvider" /> is <see langword="null" />.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="options" /> or the trust provider's declared authentication-tag bound is outside schema-1 limits.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the server-owned trust topology overlaps the governed workspace.</exception>
    public GovernedLoopRevisionLifecycleStore(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        GovernedLoopRevisionStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new GovernedLoopRevisionStoreOptions());
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > GovernedLoopRevisionStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _paths = new GovernedLoopRevisionStorePaths(paths);
        _pathGuard = new CapabilityCatalogPathGuard(
            paths.RootPath,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver);
        _trustProvider = trustProvider;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<GovernedLoopRevisionGraphReadResult> ReadGraphAsync(string graphId, CancellationToken cancellationToken = default)
    {
        if (!IsIdentifier(graphId))
        {
            return new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null);
        }

        GovernedLoopRevisionGraphReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadGraphCoreAsync(graphId, token);
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

            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult
                ?? new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopRevisionStoreReadResult> ReadForMutationAsync(
        string graphId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        if (!IsIdentifier(graphId) || !IsIdentifier(operationId) || !IsHash(requestHash))
        {
            return new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null, null);
        }

        var callbackEntered = false;
        GovernedLoopRevisionStoreReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await ReadForMutationCoreAsync(
                        graphId,
                        operationId,
                        requestHash,
                        token,
                        cancellationToken);
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

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (callbackEntered)
            {
                return new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null, null);
            }

            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult
                ?? new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<GovernedLoopRevisionStoreCommitResult> CommitAsync(
        GovernedLoopRevisionStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        if (!ValidateMutation(mutation))
        {
            return CommitResult(GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }

        var callbackEntered = false;
        GovernedLoopRevisionStoreCommitResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    callbackResult = await CommitCoreAsync(mutation, token, cancellationToken);
                    return callbackResult;
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (callbackResult is null && cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return callbackResult
                ?? CommitResult(callbackEntered
                    ? GovernedLoopRevisionStoreCommitStatus.Ambiguous
                    : GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return callbackResult
                ?? CommitResult(callbackEntered
                    ? GovernedLoopRevisionStoreCommitStatus.Ambiguous
                    : GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopRevisionGraphReadResult> ReadGraphCoreAsync(string graphId, CancellationToken cancellationToken)
    {
        await using var session = await AcquireAsync(cancellationToken);
        var workspaceIdentity = WorkspaceIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
        var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
        if (loaded.Disposition == GovernedLoopRevisionLoadDisposition.Pending || loaded.Disposition == GovernedLoopRevisionLoadDisposition.Recovered)
        {
            return new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null);
        }

        if (loaded.Document is null)
        {
            return new GovernedLoopRevisionGraphReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null);
        }

        var snapshot = Snapshot(loaded.Document, graphId);
        var status = snapshot is null ? GovernedLoopRevisionStoreReadStatus.NotFound : GovernedLoopRevisionStoreReadStatus.Ready;
        return new GovernedLoopRevisionGraphReadResult(status, loaded.Document.Generation, snapshot);
    }

    private async Task<GovernedLoopRevisionStoreReadResult> ReadForMutationCoreAsync(
        string graphId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var outcomeMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
            if (loaded.Disposition == GovernedLoopRevisionLoadDisposition.Pending)
            {
                if (!PendingMatches(loaded.Pending!, graphId, operationId, requestHash))
                {
                    return new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null, null);
                }

                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken);
            }

            if (loaded.Disposition == GovernedLoopRevisionLoadDisposition.Recovered)
            {
                return new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Ambiguous, 0, null, null);
            }

            if (loaded.Document is null)
            {
                return new GovernedLoopRevisionStoreReadResult(GovernedLoopRevisionStoreReadStatus.Unavailable, 0, null, null);
            }

            var operation = FindOperation(loaded.Document, operationId);
            var snapshot = Snapshot(loaded.Document, graphId);
            var status = snapshot is null ? GovernedLoopRevisionStoreReadStatus.NotFound : GovernedLoopRevisionStoreReadStatus.Ready;
            return new GovernedLoopRevisionStoreReadResult(status, loaded.Document.Generation, snapshot, operation);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return new GovernedLoopRevisionStoreReadResult(
                outcomeMayHaveCommitted
                    ? GovernedLoopRevisionStoreReadStatus.Ambiguous
                    : GovernedLoopRevisionStoreReadStatus.Unavailable,
                0,
                null,
                null);
        }
    }

    private async Task<GovernedLoopRevisionStoreCommitResult> CommitCoreAsync(
        GovernedLoopRevisionStoreMutation mutation,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var outcomeMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken);
            if (loaded.Disposition == GovernedLoopRevisionLoadDisposition.Pending)
            {
                if (!PendingMatches(loaded.Pending!, mutation.GraphId, mutation.Operation.OperationId, mutation.Operation.RequestHash))
                {
                    return CommitResult(GovernedLoopRevisionStoreCommitStatus.Ambiguous);
                }

                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken);
                var recoveredOperation = FindOperation(loaded.Document!, mutation.Operation.OperationId);
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.Replayed,
                    loaded.Document!.Generation,
                    recoveredOperation,
                    Snapshot(loaded.Document, mutation.GraphId));
            }

            if (loaded.Disposition == GovernedLoopRevisionLoadDisposition.Recovered)
            {
                return CommitResult(GovernedLoopRevisionStoreCommitStatus.Ambiguous);
            }

            if (loaded.Document is null)
            {
                return CommitResult(GovernedLoopRevisionStoreCommitStatus.Unavailable);
            }

            var current = loaded.Document;
            var existing = FindOperation(current, mutation.Operation.OperationId);
            if (existing is not null)
            {
                if (!string.Equals(existing.GraphId, mutation.GraphId, StringComparison.Ordinal)
                    || !string.Equals(existing.Evidence.RequestHash, mutation.Operation.RequestHash, StringComparison.Ordinal))
                {
                    return new GovernedLoopRevisionStoreCommitResult(
                        GovernedLoopRevisionStoreCommitStatus.OperationConflict,
                        current.Generation,
                        existing,
                        Snapshot(current, mutation.GraphId));
                }

                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.Replayed,
                    current.Generation,
                    existing,
                    Snapshot(current, mutation.GraphId));
            }

            if (mutation.ExpectedStoreGeneration != current.Generation)
            {
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.StoreConflict,
                    current.Generation,
                    null,
                    Snapshot(current, mutation.GraphId));
            }

            if (current.Operations.Count >= _options.MaxOperationEvidenceRecords
                || mutation.ArtifactToAppend is not null && current.Artifacts.Count >= _options.MaxRevisionArtifacts)
            {
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.Unavailable,
                    current.Generation,
                    null,
                    Snapshot(current, mutation.GraphId));
            }

            var candidate = CreateCandidate(current, mutation, workspaceIdentity);
            if (!ValidateDocumentShape(candidate, workspaceIdentity) || !IsDirectSuccessor(current, candidate))
            {
                return new GovernedLoopRevisionStoreCommitResult(
                    GovernedLoopRevisionStoreCommitStatus.Unavailable,
                    current.Generation,
                    null,
                    Snapshot(current, mutation.GraphId));
            }

            var currentDigest = ComputeContentDigest(current).Value;
            trust ??= await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken);
            if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
            {
                return CommitResult(GovernedLoopRevisionStoreCommitStatus.Unavailable);
            }

            var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken);
            var serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken);
            await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken);
            await ObserveAsync(GovernedLoopRevisionPersistenceBoundary.ProofPublished, cancellationToken);
            outcomeMayHaveCommitted = true;
            await session.WriteTextAtomicallyAsync(_paths.PrimaryPath, serializedCandidate.Json, cancellationToken);
            await ObserveAsync(GovernedLoopRevisionPersistenceBoundary.PrimaryPublished, cancellationToken);
            _ = await _trustProvider.AdvanceAsync(
                workspaceIdentity,
                trust.CurrentGeneration,
                trust.CurrentContentDigest,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                cancellationToken);
            await ObserveAsync(GovernedLoopRevisionPersistenceBoundary.TrustAdvanced, cancellationToken);
            var committed = candidate with
            {
                ContentDigest = serializedCandidate.ContentDigest,
                AuthenticationTag = serializedCandidate.AuthenticationTag
            };
            return new GovernedLoopRevisionStoreCommitResult(
                GovernedLoopRevisionStoreCommitStatus.Committed,
                committed.Generation,
                FindOperation(committed, mutation.Operation.OperationId),
                Snapshot(committed, mutation.GraphId));
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return CommitResult(outcomeMayHaveCommitted
                ? GovernedLoopRevisionStoreCommitStatus.Ambiguous
                : GovernedLoopRevisionStoreCommitStatus.Unavailable);
        }
    }

    private async Task<GovernedLoopRevisionLoadResult> LoadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        CapabilityCatalogTrustState? trust,
        CancellationToken cancellationToken)
    {
        var primaryExists = session.FileExists(_paths.PrimaryPath);
        var proofExists = session.FileExists(_paths.ProofPath);
        var empty = EmptyDocument(workspaceIdentity);
        if (trust is null)
        {
            return primaryExists || proofExists
                ? new GovernedLoopRevisionLoadResult(null, null, GovernedLoopRevisionLoadDisposition.Unavailable)
                : new GovernedLoopRevisionLoadResult(empty, null, GovernedLoopRevisionLoadDisposition.Current);
        }

        var primary = primaryExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.PrimaryPath, cancellationToken)
            : null;
        var proof = proofExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.ProofPath, cancellationToken)
            : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new GovernedLoopRevisionLoadResult(primary, null, GovernedLoopRevisionLoadDisposition.Current);
        }

        var currentBase = proof is not null && MatchesCurrent(proof, trust)
            ? proof
            : !primaryExists && !proofExists && MatchesCurrent(empty, trust)
                ? empty
                : null;
        if (primary is not null
            && currentBase is not null
            && IsAuthenticatedDirectSuccessor(currentBase, primary, trust))
        {
            return new GovernedLoopRevisionLoadResult(currentBase, primary, GovernedLoopRevisionLoadDisposition.Pending);
        }

        if (!primaryExists && currentBase is not null)
        {
            return new GovernedLoopRevisionLoadResult(currentBase, null, GovernedLoopRevisionLoadDisposition.Current);
        }

        if (currentBase is not null)
        {
            return new GovernedLoopRevisionLoadResult(currentBase, null, GovernedLoopRevisionLoadDisposition.Recovered);
        }

        if (proof is not null && MatchesPrevious(proof, trust))
        {
            return new GovernedLoopRevisionLoadResult(proof, null, GovernedLoopRevisionLoadDisposition.Recovered);
        }

        if (primary is not null && MatchesPrevious(primary, trust))
        {
            return new GovernedLoopRevisionLoadResult(primary, null, GovernedLoopRevisionLoadDisposition.Recovered);
        }

        return new GovernedLoopRevisionLoadResult(null, null, GovernedLoopRevisionLoadDisposition.Unavailable);
    }

    private async Task<GovernedLoopRevisionStoreDocument?> TryReadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaxArtifactUtf8Bytes, cancellationToken);
            if (bytes is null || HasUtf8Bom(bytes))
            {
                return null;
            }

            var text = _strictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (!GovernedLoopRevisionStoreJson.IsStrictBoundedDocument(
                    json.RootElement,
                    _options.MaxRevisionArtifacts,
                    _options.MaxGraphHeads,
                    _options.MaxOperationEvidenceRecords))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<GovernedLoopRevisionStoreDocument>(text, _jsonOptions);
            if (document is null
                || !ValidateDocumentShape(document, workspaceIdentity)
                || string.IsNullOrEmpty(document.AuthenticationTag)
                || _strictUtf8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
                || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var digest, out _)
                || !digest!.FixedTimeEquals(ComputeContentDigest(document))
                || !await _trustProvider.VerifyArtifactAsync(
                    workspaceIdentity,
                    document.Generation,
                    document.ContentDigest,
                    document.AuthenticationTag,
                    cancellationToken))
            {
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<GovernedLoopRevisionLoadResult> FinalizePendingAsync(
        string workspaceIdentity,
        CapabilityCatalogTrustState trust,
        GovernedLoopRevisionLoadResult loaded,
        CancellationToken cancellationToken)
    {
        var pending = loaded.Pending ?? throw new InvalidOperationException("A pending direct successor is required.");
        _ = await _trustProvider.AdvanceAsync(
            workspaceIdentity,
            trust.CurrentGeneration,
            trust.CurrentContentDigest,
            pending.Generation,
            pending.ContentDigest,
            cancellationToken);
        await ObserveAsync(GovernedLoopRevisionPersistenceBoundary.TrustAdvanced, cancellationToken);
        return new GovernedLoopRevisionLoadResult(pending, null, GovernedLoopRevisionLoadDisposition.Current);
    }

    private GovernedLoopRevisionStoreDocument CreateCandidate(
        GovernedLoopRevisionStoreDocument current,
        GovernedLoopRevisionStoreMutation mutation,
        string workspaceIdentity)
    {
        var artifacts = mutation.ArtifactToAppend is null
            ? current.Artifacts.ToArray()
            : current.Artifacts.Append(mutation.ArtifactToAppend).ToArray();
        var heads = mutation.HeadToWrite is null
            ? current.Heads.ToArray()
            : current.Heads
                .Where(head => !string.Equals(head.GraphId, mutation.GraphId, StringComparison.Ordinal))
                .Append(mutation.HeadToWrite)
                .OrderBy(head => head.GraphId, StringComparer.Ordinal)
                .ToArray();
        var operations = current.Operations
            .Append(new GovernedLoopRevisionStoredOperation(mutation.GraphId, mutation.Operation))
            .ToArray();
        return new GovernedLoopRevisionStoreDocument(
            GovernedLoopRevisionStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            artifacts,
            heads,
            operations,
            string.Empty,
            string.Empty);
    }

    private bool ValidateDocumentShape(GovernedLoopRevisionStoreDocument document, string workspaceIdentity)
    {
        if (document.SchemaVersion != GovernedLoopRevisionStoreDocument.CurrentSchemaVersion
            || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || document.Generation < 0
            || document.Artifacts is null
            || document.Heads is null
            || document.Operations is null
            || document.Generation != (long)document.Operations.Count
            || document.Heads.Count > _options.MaxGraphHeads
            || document.Heads.Count > GovernedLoopRevisionContractLimits.MaxGraphsPerStore
            || document.Artifacts.Count > _options.MaxRevisionArtifacts
            || document.Operations.Count > _options.MaxOperationEvidenceRecords)
        {
            return false;
        }

        if (!document.Heads.Select(head => head.GraphId)
            .SequenceEqual(document.Heads.Select(head => head.GraphId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        var heads = new Dictionary<string, GovernedLoopRevisionLifecycleHead>(StringComparer.Ordinal);
        foreach (var head in document.Heads.Take(_options.MaxGraphHeads + 1))
        {
            if (!GovernedLoopRevisionContractValidator.Validate(head).IsValid || !heads.TryAdd(head.GraphId, head))
            {
                return false;
            }
        }

        var artifactsByGraph = new Dictionary<string, List<GovernedLoopRevisionArtifact>>(StringComparer.Ordinal);
        var revisionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in document.Artifacts.Take(_options.MaxRevisionArtifacts + 1))
        {
            if (!GovernedLoopRevisionContractValidator.Validate(artifact).IsValid)
            {
                return false;
            }

            var revisionKey = artifact.Revision.GraphId + "\n" + artifact.Revision.RevisionId;
            if (!revisionIds.Add(revisionKey))
            {
                return false;
            }

            var graphArtifacts = GetOrAdd(artifactsByGraph, artifact.Revision.GraphId);
            if (graphArtifacts.Count >= GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph
                || !ArtifactContinues(graphArtifacts, artifact))
            {
                return false;
            }

            graphArtifacts.Add(artifact);
        }

        var operationsByGraph = new Dictionary<string, List<GovernedLoopRevisionOperationEvidence>>(StringComparer.Ordinal);
        var operationsById = new Dictionary<string, GovernedLoopRevisionStoredOperation>(StringComparer.Ordinal);
        foreach (var operation in document.Operations.Take(_options.MaxOperationEvidenceRecords + 1))
        {
            if (!IsIdentifier(operation.GraphId)
                || !GovernedLoopRevisionContractValidator.Validate(operation.Evidence).IsValid
                || operation.Evidence.Outcome is GovernedLoopRevisionOperationOutcome.Unknown or GovernedLoopRevisionOperationOutcome.OutcomeUnknown
                || !EvidenceTargetsGraph(operation.Evidence, operation.GraphId)
                || !operationsById.TryAdd(operation.Evidence.OperationId, operation))
            {
                return false;
            }

            var graphOperations = GetOrAdd(operationsByGraph, operation.GraphId);
            if (graphOperations.Count >= GovernedLoopRevisionContractLimits.MaxOperationsPerGraph
                || !OperationContinues(graphOperations, operation.Evidence))
            {
                return false;
            }

            graphOperations.Add(operation.Evidence);
        }

        if (heads.Count != artifactsByGraph.Count
            || heads.Count != operationsByGraph.Keys.Count(graphId => operationsByGraph[graphId].Any(operation => operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed)))
        {
            return false;
        }

        foreach (var (graphId, graphArtifacts) in artifactsByGraph)
        {
            if (!heads.TryGetValue(graphId, out var head)
                || !operationsByGraph.TryGetValue(graphId, out var graphOperations)
                || !Equals(graphOperations.LastOrDefault(operation => operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed)?.ResultHead, head)
                || !HeadReferencesArtifacts(head, graphArtifacts)
                || head.PublishedRevision is not null && !graphOperations.Any(operation => ProvesPublication(operation, head.PublishedRevision))
                || !ArtifactsHaveCreationEvidence(graphArtifacts, graphOperations)
                || !OperationsResolveArtifactHistory(graphArtifacts, graphOperations))
            {
                return false;
            }
        }

        foreach (var graphId in operationsByGraph.Keys)
        {
            var committedHead = operationsByGraph[graphId]
                .LastOrDefault(operation => operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed)
                ?.ResultHead;
            if (committedHead is null && heads.ContainsKey(graphId)
                || committedHead is not null && (!heads.TryGetValue(graphId, out var head) || !Equals(committedHead, head)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidateMutation(GovernedLoopRevisionStoreMutation? mutation)
    {
        if (mutation is null
            || !IsIdentifier(mutation.GraphId)
            || mutation.ExpectedStoreGeneration < 0
            || !GovernedLoopRevisionContractValidator.Validate(mutation.Operation).IsValid
            || mutation.Operation.Outcome is GovernedLoopRevisionOperationOutcome.Unknown or GovernedLoopRevisionOperationOutcome.OutcomeUnknown
            || !EvidenceTargetsGraph(mutation.Operation, mutation.GraphId))
        {
            return false;
        }

        var committed = mutation.Operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed;
        if (committed != (mutation.HeadToWrite is not null)
            || mutation.HeadToWrite is not null
            && (!GovernedLoopRevisionContractValidator.Validate(mutation.HeadToWrite).IsValid
                || !string.Equals(mutation.HeadToWrite.GraphId, mutation.GraphId, StringComparison.Ordinal)
                || !Equals(mutation.HeadToWrite, mutation.Operation.ResultHead)))
        {
            return false;
        }

        var requiresArtifact = committed && mutation.Operation.Kind is GovernedLoopRevisionOperationKind.CreateDraft
            or GovernedLoopRevisionOperationKind.ReplaceDraft
            or GovernedLoopRevisionOperationKind.Rollback;
        if (requiresArtifact != (mutation.ArtifactToAppend is not null))
        {
            return false;
        }

        return mutation.ArtifactToAppend is null || ArtifactMatchesOperation(mutation.ArtifactToAppend, mutation.Operation);
    }

    private static bool ArtifactMatchesOperation(
        GovernedLoopRevisionArtifact artifact,
        GovernedLoopRevisionOperationEvidence operation)
    {
        var expectedPredecessor = operation.Kind == GovernedLoopRevisionOperationKind.CreateDraft
            ? null
            : operation.PreviousHead?.DraftRevision ?? operation.PreviousHead?.PublishedRevision?.Revision;
        return GovernedLoopRevisionContractValidator.Validate(artifact).IsValid
            && string.Equals(artifact.CreationOperationId, operation.OperationId, StringComparison.Ordinal)
            && string.Equals(artifact.CreatedByActorId, operation.ActorId, StringComparison.Ordinal)
            && artifact.CreatedAtUtc == operation.RecordedAtUtc
            && SameReference(artifact.Revision, operation.CandidateRevision)
            && SameReference(artifact.PredecessorRevision, expectedPredecessor)
            && Equals(artifact.RollbackSourcePublication, operation.RollbackSourcePublication)
            && (operation.Kind == GovernedLoopRevisionOperationKind.Rollback) == (artifact.RollbackSourcePublication is not null);
    }

    private static bool IsDirectSuccessor(
        GovernedLoopRevisionStoreDocument current,
        GovernedLoopRevisionStoreDocument candidate)
    {
        if (current.Generation == long.MaxValue
            || candidate.Generation != current.Generation + 1
            || !string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            || candidate.Operations.Count != current.Operations.Count + 1
            || candidate.Artifacts.Count < current.Artifacts.Count
            || candidate.Artifacts.Count > current.Artifacts.Count + 1
            || !candidate.Operations.Take(current.Operations.Count).SequenceEqual(current.Operations)
            || !candidate.Artifacts.Take(current.Artifacts.Count).SequenceEqual(current.Artifacts))
        {
            return false;
        }

        var appended = candidate.Operations[^1];
        var currentHead = current.Heads.SingleOrDefault(head => string.Equals(head.GraphId, appended.GraphId, StringComparison.Ordinal));
        var candidateHead = candidate.Heads.SingleOrDefault(head => string.Equals(head.GraphId, appended.GraphId, StringComparison.Ordinal));
        if (appended.Evidence.Outcome == GovernedLoopRevisionOperationOutcome.Committed)
        {
            if (!Equals(appended.Evidence.PreviousHead, currentHead)
                || !Equals(appended.Evidence.ResultHead, candidateHead))
            {
                return false;
            }
        }
        else if (!Equals(currentHead, candidateHead))
        {
            return false;
        }

        return candidate.Heads
                .Where(head => !string.Equals(head.GraphId, appended.GraphId, StringComparison.Ordinal))
                .SequenceEqual(current.Heads.Where(head => !string.Equals(head.GraphId, appended.GraphId, StringComparison.Ordinal)))
            && (candidate.Artifacts.Count == current.Artifacts.Count
                || ArtifactMatchesOperation(candidate.Artifacts[^1], appended.Evidence));
    }

    private static bool IsAuthenticatedDirectSuccessor(
        GovernedLoopRevisionStoreDocument current,
        GovernedLoopRevisionStoreDocument pending,
        CapabilityCatalogTrustState trust)
    {
        return MatchesCurrent(current, trust)
            && pending.Generation == trust.CurrentGeneration + 1
            && IsDirectSuccessor(current, pending);
    }

    private static bool ArtifactContinues(
        IReadOnlyList<GovernedLoopRevisionArtifact> existing,
        GovernedLoopRevisionArtifact candidate)
    {
        return existing.Count == 0
            ? candidate.PredecessorRevision is null
            : SameReference(candidate.PredecessorRevision, existing[^1].Revision);
    }

    private static bool OperationContinues(
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> existing,
        GovernedLoopRevisionOperationEvidence candidate)
    {
        var current = existing.LastOrDefault(operation => operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed)?.ResultHead;
        return Equals(candidate.PreviousHead, current)
            && (candidate.Outcome != GovernedLoopRevisionOperationOutcome.Committed
                || candidate.ResultHead is not null);
    }

    private static bool ArtifactsHaveCreationEvidence(
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations)
    {
        var previousCreationIndex = -1;
        foreach (var artifact in artifacts)
        {
            var index = IndexOfOperation(operations, artifact.CreationOperationId);
            if (index <= previousCreationIndex
                || operations[index].Outcome != GovernedLoopRevisionOperationOutcome.Committed
                || !ArtifactMatchesOperation(artifact, operations[index]))
            {
                return false;
            }

            if (artifact.RollbackSourcePublication is not null
                && !operations.Take(index).Any(operation => ProvesPublication(operation, artifact.RollbackSourcePublication)))
            {
                return false;
            }

            previousCreationIndex = index;
        }

        return true;
    }

    private static bool OperationsResolveArtifactHistory(
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations)
    {
        var creationIndexes = artifacts.ToDictionary(
            artifact => RevisionKey(artifact.Revision),
            artifact => IndexOfOperation(operations, artifact.CreationOperationId),
            StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var committed = operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed;
            if (committed && operation.TargetRevision is not null && !WasCreatedBefore(operation.TargetRevision, creationIndexes, index)
                || committed && operation.RollbackSourcePublication is not null
                && (!WasCreatedBefore(operation.RollbackSourcePublication.Revision, creationIndexes, index)
                    || !operations.Take(index).Any(prior => ProvesPublication(prior, operation.RollbackSourcePublication)))
                || !HeadResolves(operation.PreviousHead, artifacts, creationIndexes, operations, index - 1)
                || !HeadResolves(operation.ResultHead, artifacts, creationIndexes, operations, index))
            {
                return false;
            }

            if (operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed
                && operation.Kind is GovernedLoopRevisionOperationKind.CreateDraft
                    or GovernedLoopRevisionOperationKind.ReplaceDraft
                    or GovernedLoopRevisionOperationKind.Rollback
                && (operation.CandidateRevision is null
                    || !creationIndexes.TryGetValue(RevisionKey(operation.CandidateRevision), out var creationIndex)
                    || creationIndex != index))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HeadResolves(
        GovernedLoopRevisionLifecycleHead? head,
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts,
        IReadOnlyDictionary<string, int> creationIndexes,
        IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations,
        int maximumOperationIndex)
    {
        if (head is null)
        {
            return true;
        }

        return (head.DraftRevision is null
                || WasCreatedBy(head.DraftRevision, creationIndexes, maximumOperationIndex))
            && (head.PublishedRevision is null
                || WasCreatedBy(head.PublishedRevision.Revision, creationIndexes, maximumOperationIndex)
                && operations.Take(maximumOperationIndex + 1).Any(operation => ProvesPublication(operation, head.PublishedRevision)))
            && HeadReferencesArtifacts(head, artifacts);
    }

    private static bool WasCreatedBefore(
        GovernedLoopRevisionReference revision,
        IReadOnlyDictionary<string, int> creationIndexes,
        int operationIndex)
        => creationIndexes.TryGetValue(RevisionKey(revision), out var creationIndex) && creationIndex < operationIndex;

    private static bool WasCreatedBy(
        GovernedLoopRevisionReference revision,
        IReadOnlyDictionary<string, int> creationIndexes,
        int maximumOperationIndex)
        => creationIndexes.TryGetValue(RevisionKey(revision), out var creationIndex) && creationIndex <= maximumOperationIndex;

    private static string RevisionKey(GovernedLoopRevisionReference revision)
        => revision.GraphId + "\n" + revision.RevisionId;

    private static bool ProvesPublication(
        GovernedLoopRevisionOperationEvidence operation,
        GovernedLoopRevisionPublicationPin pin)
    {
        return operation.Outcome == GovernedLoopRevisionOperationOutcome.Committed
            && operation.Kind is GovernedLoopRevisionOperationKind.Publish or GovernedLoopRevisionOperationKind.Rollback
            && Equals(operation.ResultHead?.PublishedRevision, pin);
    }

    private static bool HeadReferencesArtifacts(
        GovernedLoopRevisionLifecycleHead head,
        IReadOnlyList<GovernedLoopRevisionArtifact> artifacts)
    {
        return (head.DraftRevision is null || artifacts.Any(artifact => SameReference(artifact.Revision, head.DraftRevision)))
            && (head.PublishedRevision is null || artifacts.Any(artifact => SameReference(artifact.Revision, head.PublishedRevision.Revision)));
    }

    private static bool EvidenceTargetsGraph(GovernedLoopRevisionOperationEvidence evidence, string graphId)
    {
        var references = new[]
        {
            evidence.PreviousHead?.GraphId,
            evidence.ResultHead?.GraphId,
            evidence.CandidateRevision?.GraphId,
            evidence.TargetRevision?.GraphId,
            evidence.RollbackSourcePublication?.Revision.GraphId
        };
        return references.Any(value => value is not null)
            && references.Where(value => value is not null).All(value => string.Equals(value, graphId, StringComparison.Ordinal));
    }

    private static GovernedLoopRevisionStoreSnapshot? Snapshot(
        GovernedLoopRevisionStoreDocument document,
        string graphId)
    {
        var head = document.Heads.SingleOrDefault(item => string.Equals(item.GraphId, graphId, StringComparison.Ordinal));
        if (head is null)
        {
            return null;
        }

        var artifacts = document.Artifacts
            .Where(item => string.Equals(item.Revision.GraphId, graphId, StringComparison.Ordinal))
            .Take(GovernedLoopRevisionContractLimits.MaxArtifactsPerGraph + 1)
            .ToArray();
        var operations = document.Operations
            .Where(item => string.Equals(item.GraphId, graphId, StringComparison.Ordinal))
            .Take(GovernedLoopRevisionContractLimits.MaxOperationsPerGraph + 1)
            .Select(item => item.Evidence)
            .ToArray();
        return new GovernedLoopRevisionStoreSnapshot(head, artifacts, operations);
    }

    private static GovernedLoopRevisionStoredOperation? FindOperation(
        GovernedLoopRevisionStoreDocument document,
        string operationId)
        => document.Operations.SingleOrDefault(operation => string.Equals(operation.Evidence.OperationId, operationId, StringComparison.Ordinal));

    private static bool PendingMatches(
        GovernedLoopRevisionStoreDocument pending,
        string graphId,
        string operationId,
        string requestHash)
    {
        var operation = pending.Operations[^1];
        return string.Equals(operation.GraphId, graphId, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(operation.Evidence.RequestHash, requestHash, StringComparison.Ordinal);
    }

    private async Task<GovernedLoopRevisionSerializedDocument> SerializeAsync(
        string workspaceIdentity,
        GovernedLoopRevisionStoreDocument document,
        CancellationToken cancellationToken)
    {
        var digest = ComputeContentDigest(document).Value;
        var authenticationTag = await _trustProvider.AuthenticateArtifactAsync(
            workspaceIdentity,
            document.Generation,
            digest,
            cancellationToken);
        if (string.IsNullOrEmpty(authenticationTag)
            || _strictUtf8.GetByteCount(authenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The trust provider returned an authentication tag outside its declared bound.");
        }

        var authenticated = document with { ContentDigest = digest, AuthenticationTag = authenticationTag };
        var json = JsonSerializer.Serialize(authenticated, _jsonOptions) + Environment.NewLine;
        if (_strictUtf8.GetByteCount(json) > _options.MaxArtifactUtf8Bytes)
        {
            throw new IOException("The bounded governed-loop revision artifact limit would be exceeded.");
        }

        return new GovernedLoopRevisionSerializedDocument(json, digest, authenticationTag);
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(GovernedLoopRevisionStoreDocument document)
    {
        var content = JsonSerializer.Serialize(
            document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty },
            _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private static GovernedLoopRevisionStoreDocument EmptyDocument(string workspaceIdentity)
    {
        var empty = new GovernedLoopRevisionStoreDocument(
            GovernedLoopRevisionStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            0,
            [],
            [],
            [],
            string.Empty,
            string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private async Task<CapabilityCatalogPathSession> AcquireAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.LockPath, createRoot: false, cancellationToken)
            ?? throw new IOException("The governed-loop revision workspace root is unavailable.");

    private static string WorkspaceIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(
            "embodysense-governed-loop-revisions-v1\n" + session.PhysicalIdentityMaterial);

    private async ValueTask ObserveAsync(
        GovernedLoopRevisionPersistenceBoundary boundary,
        CancellationToken cancellationToken)
    {
        if (_options.DurableBoundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken);
        }
    }

    private static GovernedLoopRevisionStoreOptions ValidateOptions(GovernedLoopRevisionStoreOptions options)
    {
        if (options.MaxGraphHeads is < 1 or > GovernedLoopRevisionStoreOptions.MaximumGraphHeads
            || options.MaxRevisionArtifacts is < 1 or > GovernedLoopRevisionStoreOptions.MaximumRevisionArtifacts
            || options.MaxOperationEvidenceRecords is < 1 or > GovernedLoopRevisionStoreOptions.MaximumOperationEvidenceRecords
            || options.MaxArtifactUtf8Bytes is < 1 or > GovernedLoopRevisionStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Governed-loop revision store options must remain within schema-1 bounds.");
        }

        return options;
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            MaxDepth = 64,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) }
        };
        options.Converters.Add(new GovernedLoopRevisionReferenceJsonConverter());
        return options;
    }

    private static bool MatchesCurrent(GovernedLoopRevisionStoreDocument document, CapabilityCatalogTrustState trust)
        => document.Generation == trust.CurrentGeneration
            && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(GovernedLoopRevisionStoreDocument document, CapabilityCatalogTrustState trust)
        => document.Generation == trust.PreviousGeneration
            && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);

    private static bool IsIdentifier(string? value)
        => CustomLoopArtifactIdentifier.IsValid(value, GovernedLoopRevisionContractLimits.MaxIdentifierCharacters);

    private static bool IsHash(string? value)
        => value is { Length: GovernedLoopRevisionContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool SameReference(GovernedLoopRevisionReference? left, GovernedLoopRevisionReference? right)
        => Equals(left, right);

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static List<TValue> GetOrAdd<TValue>(Dictionary<string, List<TValue>> values, string key)
    {
        if (!values.TryGetValue(key, out var result))
        {
            result = [];
            values.Add(key, result);
        }

        return result;
    }

    private static int IndexOfOperation(IReadOnlyList<GovernedLoopRevisionOperationEvidence> operations, string operationId)
    {
        for (var index = 0; index < operations.Count; index++)
        {
            if (string.Equals(operations[index].OperationId, operationId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static GovernedLoopRevisionStoreCommitResult CommitResult(GovernedLoopRevisionStoreCommitStatus status)
        => new(status, 0, null, null);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or FormatException
            or OverflowException
            or DecoderFallbackException
            or CryptographicException
            or InvalidOperationException;

}
