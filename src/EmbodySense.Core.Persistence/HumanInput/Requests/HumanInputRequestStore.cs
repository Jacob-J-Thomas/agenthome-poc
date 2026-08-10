using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Models;
using EmbodySense.Core.Persistence.HumanInput.Requests.Serialization;

namespace EmbodySense.Core.Persistence.HumanInput.Requests;

/// <summary>Persists one bounded, authenticated, append-only Human Input request lifecycle ledger.</summary>
/// <remarks>
/// Full request versions remain private store state; public operation evidence contains only exact references and safe
/// attribution. Workspace artifacts are untrusted until authenticated against a server-owned monotonic trust anchor bound
/// to the physical workspace. A pending direct successor can be finalized only by an exact operation retry. Recovered
/// last-proved state is read-only and never becomes a mutation base.
/// </remarks>
public sealed class HumanInputRequestStore : IHumanInputRequestLifecycleStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly HumanInputRequestStorePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;
    private readonly HumanInputRequestStoreOptions _options;

    /// <summary>Creates a Human Input request store with the default server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="options">Optional bounded store and deterministic observer options.</param>
    /// <param name="durabilityBarrier">An optional trusted post-rename durability adapter.</param>
    /// <param name="authorityTransaction">An optional shared workspace authority transaction.</param>
    public HumanInputRequestStore(
        WorkspacePaths paths,
        HumanInputRequestStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), options, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates a Human Input request store over an explicit server-owned trust provider.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="trustProvider">The server-owned provider authenticating this physical workspace ledger.</param>
    /// <param name="options">Optional bounded store and deterministic observer options.</param>
    /// <param name="durabilityBarrier">An optional trusted post-rename durability adapter.</param>
    /// <param name="authorityTransaction">An optional shared workspace authority transaction.</param>
    public HumanInputRequestStore(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        HumanInputRequestStoreOptions? options = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new HumanInputRequestStoreOptions());
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > _options.MaxArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _paths = new HumanInputRequestStorePaths(paths);
        _pathGuard = new CapabilityCatalogPathGuard(
            paths.RootPath,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver);
        _trustProvider = trustProvider;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<HumanInputRequestLifecycleStoreReadResult> ReadAsync(string requestId, CancellationToken cancellationToken = default)
    {
        if (!HumanInputIdentifier.IsValid(requestId))
        {
            return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Unavailable);
        }

        HumanInputRequestLifecycleStoreReadResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    completed = await ReadCoreAsync(requestId, token).ConfigureAwait(false);
                    return completed;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (completed is null && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return completed ?? ReadResult(HumanInputRequestLifecycleStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<HumanInputRequestLifecycleStoreReadResult> ReadForMutationAsync(
        string requestId,
        string operationId,
        string requestHash,
        string? relatedRequestId = null,
        CancellationToken cancellationToken = default)
    {
        if (!HumanInputIdentifier.IsValid(requestId)
            || !HumanInputIdentifier.IsValid(operationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters)
            || !IsHash(requestHash)
            || relatedRequestId is not null
                && (!HumanInputIdentifier.IsValid(relatedRequestId)
                    || string.Equals(relatedRequestId, requestId, StringComparison.Ordinal)))
        {
            return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Unavailable);
        }

        var callbackEntered = false;
        HumanInputRequestLifecycleStoreReadResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    completed = await ReadForMutationCoreAsync(
                        requestId,
                        operationId,
                        requestHash,
                        relatedRequestId,
                        token,
                        cancellationToken).ConfigureAwait(false);
                    return completed;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (completed is null && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return completed ?? ReadResult(callbackEntered
                ? HumanInputRequestLifecycleStoreReadStatus.Ambiguous
                : HumanInputRequestLifecycleStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    public async Task<HumanInputRequestLifecycleStoreCommitResult> CommitAsync(
        HumanInputRequestLifecycleStoreMutation mutation,
        CancellationToken cancellationToken = default)
    {
        if (!TryCaptureMutation(mutation, out var captured))
        {
            return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Unavailable);
        }

        var callbackEntered = false;
        HumanInputRequestLifecycleStoreCommitResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    completed = await CommitCoreAsync(captured!, token, cancellationToken).ConfigureAwait(false);
                    return completed;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (completed is null && cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return completed ?? CommitResult(callbackEntered
                ? HumanInputRequestLifecycleStoreCommitStatus.Ambiguous
                : HumanInputRequestLifecycleStoreCommitStatus.Unavailable);
        }
    }

    private async Task<HumanInputRequestLifecycleStoreReadResult> ReadCoreAsync(string requestId, CancellationToken cancellationToken)
    {
        await using var session = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var workspaceIdentity = WorkspaceIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken).ConfigureAwait(false);
        if (loaded.Disposition is HumanInputRequestLoadDisposition.Pending or HumanInputRequestLoadDisposition.Recovered)
        {
            return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Ambiguous);
        }

        if (loaded.Document is null)
        {
            return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Unavailable);
        }

        var snapshot = Snapshot(loaded.Document, requestId);
        return new HumanInputRequestLifecycleStoreReadResult(
            snapshot is null ? HumanInputRequestLifecycleStoreReadStatus.NotFound : HumanInputRequestLifecycleStoreReadStatus.Ready,
            loaded.Document.Generation,
            snapshot,
            null,
            null);
    }

    private async Task<HumanInputRequestLifecycleStoreReadResult> ReadForMutationCoreAsync(
        string requestId,
        string operationId,
        string requestHash,
        string? relatedRequestId,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var outcomeMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken).ConfigureAwait(false);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken).ConfigureAwait(false);
            if (loaded.Disposition == HumanInputRequestLoadDisposition.Pending)
            {
                if (!PendingMatches(loaded.Pending!, requestId, operationId, requestHash, relatedRequestId))
                {
                    return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Ambiguous);
                }

                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken).ConfigureAwait(false);
            }

            if (loaded.Disposition == HumanInputRequestLoadDisposition.Recovered)
            {
                return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Ambiguous);
            }

            if (loaded.Document is null)
            {
                return ReadResult(HumanInputRequestLifecycleStoreReadStatus.Unavailable);
            }

            var document = loaded.Document;
            var primary = Snapshot(document, requestId);
            var requestedRelated = relatedRequestId is null ? null : Snapshot(document, relatedRequestId);
            var operation = FindOperation(document, operationId);
            if (operation is null)
            {
                return new HumanInputRequestLifecycleStoreReadResult(
                    primary is null ? HumanInputRequestLifecycleStoreReadStatus.NotFound : HumanInputRequestLifecycleStoreReadStatus.Ready,
                    document.Generation,
                    primary,
                    requestedRelated,
                    null);
            }

            var exact = string.Equals(operation.RequestId, requestId, StringComparison.Ordinal)
                && string.Equals(operation.Evidence.RequestHash, requestHash, StringComparison.Ordinal)
                && (relatedRequestId is null
                    || string.Equals(operation.Evidence.RelatedRequestId, relatedRequestId, StringComparison.Ordinal));
            var related = exact && operation.Evidence.RelatedRequestId is { } relatedId
                ? Snapshot(document, relatedId)
                : null;
            return new HumanInputRequestLifecycleStoreReadResult(
                exact
                    ? primary is null ? HumanInputRequestLifecycleStoreReadStatus.NotFound : HumanInputRequestLifecycleStoreReadStatus.Ready
                    : HumanInputRequestLifecycleStoreReadStatus.OperationConflict,
                document.Generation,
                primary,
                related,
                operation);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return ReadResult(outcomeMayHaveCommitted
                ? HumanInputRequestLifecycleStoreReadStatus.Ambiguous
                : HumanInputRequestLifecycleStoreReadStatus.Unavailable);
        }
    }

    private async Task<HumanInputRequestLifecycleStoreCommitResult> CommitCoreAsync(
        HumanInputRequestLifecycleStoreMutation mutation,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        var outcomeMayHaveCommitted = false;
        try
        {
            await using var session = await AcquireAsync(cancellationToken).ConfigureAwait(false);
            var workspaceIdentity = WorkspaceIdentity(session);
            var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken).ConfigureAwait(false);
            var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken).ConfigureAwait(false);
            if (loaded.Disposition == HumanInputRequestLoadDisposition.Pending)
            {
                if (!PendingMatches(loaded.Pending!, mutation))
                {
                    return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous);
                }

                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken).ConfigureAwait(false);
                return CommitProjection(HumanInputRequestLifecycleStoreCommitStatus.Replayed, loaded.Document!, mutation.Operation.OperationId);
            }

            if (loaded.Disposition == HumanInputRequestLoadDisposition.Recovered)
            {
                return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous);
            }

            if (loaded.Document is null)
            {
                return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Unavailable);
            }

            var current = loaded.Document;
            var existing = FindOperation(current, mutation.Operation.OperationId);
            if (existing is not null)
            {
                return Equals(existing.Evidence, mutation.Operation)
                    ? CommitProjection(HumanInputRequestLifecycleStoreCommitStatus.Replayed, current, mutation.Operation.OperationId)
                    : new HumanInputRequestLifecycleStoreCommitResult(
                        HumanInputRequestLifecycleStoreCommitStatus.OperationConflict,
                        current.Generation,
                        existing,
                        Snapshot(current, mutation.Operation.TargetRequestId),
                        null);
            }

            if (mutation.ExpectedStoreGeneration != current.Generation)
            {
                return new HumanInputRequestLifecycleStoreCommitResult(
                    HumanInputRequestLifecycleStoreCommitStatus.StoreConflict,
                    current.Generation,
                    null,
                    Snapshot(current, mutation.Operation.TargetRequestId),
                    RelatedSnapshot(current, mutation.Operation));
            }

            if (WouldExceedCountLimit(current, mutation))
            {
                return new HumanInputRequestLifecycleStoreCommitResult(
                    HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded,
                    current.Generation,
                    null,
                    Snapshot(current, mutation.Operation.TargetRequestId),
                    RelatedSnapshot(current, mutation.Operation));
            }

            var candidate = CreateCandidate(current, mutation, workspaceIdentity);
            if (!HumanInputRequestStoreStateValidator.Validate(candidate, workspaceIdentity, _options)
                || !HumanInputRequestStoreStateValidator.IsDirectSuccessor(current, candidate))
            {
                return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Unavailable);
            }

            if (WouldExceedArtifactLimit(candidate))
            {
                return new HumanInputRequestLifecycleStoreCommitResult(
                    HumanInputRequestLifecycleStoreCommitStatus.LimitExceeded,
                    current.Generation,
                    null,
                    Snapshot(current, mutation.Operation.TargetRequestId),
                    RelatedSnapshot(current, mutation.Operation));
            }

            var currentDigest = ComputeContentDigest(current).Value;
            if (trust is null)
            {
                trust = await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken).ConfigureAwait(false);
                await ObserveAsync(HumanInputRequestPersistenceBoundary.TrustInitialized, cancellationToken).ConfigureAwait(false);
            }

            if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
            {
                return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Unavailable);
            }

            var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken).ConfigureAwait(false);
            var serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken).ConfigureAwait(false);
            await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken).ConfigureAwait(false);
            await ObserveAsync(HumanInputRequestPersistenceBoundary.ProofPublished, cancellationToken).ConfigureAwait(false);
            outcomeMayHaveCommitted = true;
            await session.WriteTextAtomicallyAsync(_paths.PrimaryPath, serializedCandidate.Json, cancellationToken).ConfigureAwait(false);
            await ObserveAsync(HumanInputRequestPersistenceBoundary.PrimaryPublished, cancellationToken).ConfigureAwait(false);
            var advanced = await _trustProvider.AdvanceAsync(
                workspaceIdentity,
                trust.CurrentGeneration,
                trust.CurrentContentDigest,
                candidate.Generation,
                serializedCandidate.ContentDigest,
                cancellationToken).ConfigureAwait(false);
            RequireExactAdvance(workspaceIdentity, trust, candidate.Generation, serializedCandidate.ContentDigest, advanced);
            await ObserveAsync(HumanInputRequestPersistenceBoundary.TrustAdvanced, cancellationToken).ConfigureAwait(false);
            var committed = candidate with
            {
                ContentDigest = serializedCandidate.ContentDigest,
                AuthenticationTag = serializedCandidate.AuthenticationTag
            };
            return CommitProjection(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed, mutation.Operation.OperationId);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return CommitResult(outcomeMayHaveCommitted
                ? HumanInputRequestLifecycleStoreCommitStatus.Ambiguous
                : HumanInputRequestLifecycleStoreCommitStatus.Unavailable);
        }
    }

    private async Task<HumanInputRequestLoadResult> LoadAsync(
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
                ? new HumanInputRequestLoadResult(null, null, HumanInputRequestLoadDisposition.Unavailable)
                : new HumanInputRequestLoadResult(empty, null, HumanInputRequestLoadDisposition.Current);
        }

        var primary = primaryExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.PrimaryPath, cancellationToken).ConfigureAwait(false)
            : null;
        var proof = proofExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.ProofPath, cancellationToken).ConfigureAwait(false)
            : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new HumanInputRequestLoadResult(primary, null, HumanInputRequestLoadDisposition.Current);
        }

        var currentBase = proof is not null && MatchesCurrent(proof, trust)
            ? proof
            : !primaryExists && !proofExists && MatchesCurrent(empty, trust)
                ? empty
                : null;
        if (primary is not null
            && currentBase is not null
            && MatchesCurrent(currentBase, trust)
            && primary.Generation == trust.CurrentGeneration + 1
            && HumanInputRequestStoreStateValidator.IsDirectSuccessor(currentBase, primary))
        {
            return new HumanInputRequestLoadResult(currentBase, primary, HumanInputRequestLoadDisposition.Pending);
        }

        if (!primaryExists && currentBase is not null)
        {
            return new HumanInputRequestLoadResult(currentBase, null, HumanInputRequestLoadDisposition.Current);
        }

        if (currentBase is not null)
        {
            return new HumanInputRequestLoadResult(currentBase, null, HumanInputRequestLoadDisposition.Recovered);
        }

        if (proof is not null && MatchesPrevious(proof, trust))
        {
            return new HumanInputRequestLoadResult(proof, null, HumanInputRequestLoadDisposition.Recovered);
        }

        if (primary is not null && MatchesPrevious(primary, trust))
        {
            return new HumanInputRequestLoadResult(primary, null, HumanInputRequestLoadDisposition.Recovered);
        }

        return new HumanInputRequestLoadResult(null, null, HumanInputRequestLoadDisposition.Unavailable);
    }

    private async Task<HumanInputRequestStoreDocument?> TryReadAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaxArtifactUtf8Bytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null || HasUtf8Bom(bytes))
            {
                return null;
            }

            var text = _strictUtf8.GetString(bytes);
            using var json = JsonDocument.Parse(
                text,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = HumanInputRequestLifecycleContractLimits.MaxJsonDepth
                });
            if (!HumanInputRequestStoreJson.IsStrictBoundedDocument(
                    json.RootElement,
                    _options.MaxRequestVersions,
                    _options.MaxRequests,
                    _options.MaxOperations))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<HumanInputRequestStoreDocument>(text, _jsonOptions);
            if (!HumanInputRequestStoreStateValidator.Validate(document, workspaceIdentity, _options)
                || string.IsNullOrEmpty(document!.AuthenticationTag)
                || _strictUtf8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
                || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var digest, out _)
                || !digest!.FixedTimeEquals(ComputeContentDigest(document))
                || !await _trustProvider.VerifyArtifactAsync(
                    workspaceIdentity,
                    document.Generation,
                    document.ContentDigest,
                    document.AuthenticationTag,
                    cancellationToken).ConfigureAwait(false))
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

    private async Task<HumanInputRequestLoadResult> FinalizePendingAsync(
        string workspaceIdentity,
        CapabilityCatalogTrustState trust,
        HumanInputRequestLoadResult loaded,
        CancellationToken cancellationToken)
    {
        var pending = loaded.Pending ?? throw new InvalidOperationException("A pending direct successor is required.");
        var advanced = await _trustProvider.AdvanceAsync(
            workspaceIdentity,
            trust.CurrentGeneration,
            trust.CurrentContentDigest,
            pending.Generation,
            pending.ContentDigest,
            cancellationToken).ConfigureAwait(false);
        RequireExactAdvance(workspaceIdentity, trust, pending.Generation, pending.ContentDigest, advanced);
        await ObserveAsync(HumanInputRequestPersistenceBoundary.TrustAdvanced, cancellationToken).ConfigureAwait(false);
        return new HumanInputRequestLoadResult(pending, null, HumanInputRequestLoadDisposition.Current);
    }

    private static HumanInputRequestStoreDocument CreateCandidate(
        HumanInputRequestStoreDocument current,
        HumanInputRequestLifecycleStoreMutation mutation,
        string workspaceIdentity)
    {
        var requestVersions = mutation.RequestToAppend is null
            ? current.RequestVersions.ToArray()
            : current.RequestVersions.Append(mutation.RequestToAppend).ToArray();
        var replacedIds = new HashSet<string>(StringComparer.Ordinal);
        if (mutation.PrimaryHeadToWrite is { } primary)
        {
            replacedIds.Add(primary.RequestId);
        }
        if (mutation.SecondaryHeadToWrite is { } secondary)
        {
            replacedIds.Add(secondary.RequestId);
        }

        var heads = current.Heads
            .Where(head => !replacedIds.Contains(head.RequestId))
            .Concat(mutation.PrimaryHeadToWrite is null ? [] : [mutation.PrimaryHeadToWrite])
            .Concat(mutation.SecondaryHeadToWrite is null ? [] : [mutation.SecondaryHeadToWrite])
            .OrderBy(head => head.RequestId, StringComparer.Ordinal)
            .ToArray();
        return new HumanInputRequestStoreDocument(
            HumanInputRequestStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            requestVersions,
            heads,
            current.Operations.Append(mutation.Operation).ToArray(),
            string.Empty,
            string.Empty);
    }

    private static bool TryCaptureMutation(
        HumanInputRequestLifecycleStoreMutation? mutation,
        out HumanInputRequestLifecycleStoreMutation? captured)
    {
        captured = null;
        if (mutation is null
            || mutation.ExpectedStoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || !HumanInputRequestLifecycleValidator.ValidateEvidence(mutation.Operation).IsValid)
        {
            return false;
        }

        HumanInputRequest? request = null;
        if (mutation.RequestToAppend is not null
            && !HumanInputRequestSnapshot.TryCapture(mutation.RequestToAppend, out request, out _))
        {
            return false;
        }

        var operation = mutation.Operation;
        var committed = operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed;
        var appendsRequest = committed
            && operation.Kind is HumanInputRequestLifecycleOperationKind.Create
                or HumanInputRequestLifecycleOperationKind.Reroute
                or HumanInputRequestLifecycleOperationKind.Amend
                or HumanInputRequestLifecycleOperationKind.Supersede;
        if (appendsRequest != (request is not null)
            || request is not null && (operation.CandidateRequest is null || !operation.CandidateRequest.Matches(request))
            || committed != (mutation.PrimaryHeadToWrite is not null)
            || committed && !Equals(mutation.PrimaryHeadToWrite, operation.ResultHead)
            || !committed && mutation.PrimaryHeadToWrite is not null
            || committed && !Equals(mutation.SecondaryHeadToWrite, operation.RelatedResultHead)
            || !committed && mutation.SecondaryHeadToWrite is not null
            || (mutation.SecondaryHeadToWrite is not null) != (committed && operation.Kind == HumanInputRequestLifecycleOperationKind.Supersede))
        {
            return false;
        }

        captured = mutation with { RequestToAppend = request };
        return true;
    }

    private bool WouldExceedCountLimit(
        HumanInputRequestStoreDocument current,
        HumanInputRequestLifecycleStoreMutation mutation)
    {
        if (current.Operations.Count >= _options.MaxOperations
            || mutation.RequestToAppend is not null && current.RequestVersions.Count >= _options.MaxRequestVersions)
        {
            return true;
        }

        var addsHead = mutation.Operation.Outcome == HumanInputRequestLifecycleOperationOutcome.Committed
            && mutation.Operation.Kind is HumanInputRequestLifecycleOperationKind.Create or HumanInputRequestLifecycleOperationKind.Supersede;
        if (addsHead && current.Heads.Count >= _options.MaxRequests)
        {
            return true;
        }

        var targetOperations = current.Operations.Count(operation =>
            string.Equals(operation.TargetRequestId, mutation.Operation.TargetRequestId, StringComparison.Ordinal)
            || string.Equals(operation.RelatedRequestId, mutation.Operation.TargetRequestId, StringComparison.Ordinal));
        if (targetOperations >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest)
        {
            return true;
        }

        if (mutation.Operation.RelatedRequestId is { } related)
        {
            var relatedOperations = current.Operations.Count(operation =>
                string.Equals(operation.TargetRequestId, related, StringComparison.Ordinal)
                || string.Equals(operation.RelatedRequestId, related, StringComparison.Ordinal));
            if (relatedOperations >= HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest)
            {
                return true;
            }
        }

        return mutation.RequestToAppend is { } request
            && current.RequestVersions.Count(version => string.Equals(version.RequestId, request.RequestId, StringComparison.Ordinal))
                >= HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerRequest;
    }

    private bool WouldExceedArtifactLimit(HumanInputRequestStoreDocument document)
    {
        var withoutProof = JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _jsonOptions) + Environment.NewLine;
        var maximumEscapedAuthenticationTagBytes = checked(_trustProvider.MaximumAuthenticationTagUtf8Bytes * 6);
        return _strictUtf8.GetByteCount(withoutProof) + 64 + maximumEscapedAuthenticationTagBytes > _options.MaxArtifactUtf8Bytes;
    }

    private static bool PendingMatches(
        HumanInputRequestStoreDocument pending,
        string requestId,
        string operationId,
        string requestHash,
        string? relatedRequestId)
    {
        var operation = pending.Operations[^1];
        return string.Equals(operation.TargetRequestId, requestId, StringComparison.Ordinal)
            && string.Equals(operation.OperationId, operationId, StringComparison.Ordinal)
            && string.Equals(operation.RequestHash, requestHash, StringComparison.Ordinal)
            && (relatedRequestId is null
                || string.Equals(operation.RelatedRequestId, relatedRequestId, StringComparison.Ordinal));
    }

    private static bool PendingMatches(
        HumanInputRequestStoreDocument pending,
        HumanInputRequestLifecycleStoreMutation mutation)
    {
        var operation = pending.Operations[^1];
        if (!Equals(operation, mutation.Operation))
        {
            return false;
        }

        if (mutation.RequestToAppend is not null
            && !mutation.Operation.CandidateRequest!.Matches(pending.RequestVersions[^1]))
        {
            return false;
        }

        return mutation.PrimaryHeadToWrite is null
                || pending.Heads.Any(head => Equals(head, mutation.PrimaryHeadToWrite))
            && (mutation.SecondaryHeadToWrite is null
                || pending.Heads.Any(head => Equals(head, mutation.SecondaryHeadToWrite)));
    }

    private async Task<HumanInputRequestSerializedDocument> SerializeAsync(
        string workspaceIdentity,
        HumanInputRequestStoreDocument document,
        CancellationToken cancellationToken)
    {
        var digest = ComputeContentDigest(document).Value;
        var authenticationTag = await _trustProvider.AuthenticateArtifactAsync(workspaceIdentity, document.Generation, digest, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(authenticationTag)
            || _strictUtf8.GetByteCount(authenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The trust provider returned an authentication tag outside its declared bound.");
        }

        var authenticated = document with { ContentDigest = digest, AuthenticationTag = authenticationTag };
        var json = JsonSerializer.Serialize(authenticated, _jsonOptions) + Environment.NewLine;
        if (_strictUtf8.GetByteCount(json) > _options.MaxArtifactUtf8Bytes)
        {
            throw new IOException("The bounded Human Input request artifact limit would be exceeded.");
        }

        return new HumanInputRequestSerializedDocument(json, digest, authenticationTag);
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(HumanInputRequestStoreDocument document)
    {
        var content = JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _hashOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content));
    }

    private static HumanInputRequestStoreDocument EmptyDocument(string workspaceIdentity)
    {
        var empty = new HumanInputRequestStoreDocument(
            HumanInputRequestStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            0,
            [],
            [],
            [],
            string.Empty,
            string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private static HumanInputRequestLifecycleStoreSnapshot? Snapshot(HumanInputRequestStoreDocument document, string requestId)
    {
        var head = document.Heads.SingleOrDefault(value => string.Equals(value.RequestId, requestId, StringComparison.Ordinal));
        if (head is null)
        {
            return null;
        }

        var requests = new List<HumanInputRequest>();
        foreach (var request in document.RequestVersions.Where(value => string.Equals(value.RequestId, requestId, StringComparison.Ordinal)))
        {
            if (!HumanInputRequestSnapshot.TryCapture(request, out var captured, out _) || captured is null)
            {
                return null;
            }

            requests.Add(captured);
        }

        var operations = document.Operations
            .Where(operation => string.Equals(operation.TargetRequestId, requestId, StringComparison.Ordinal)
                || string.Equals(operation.RelatedRequestId, requestId, StringComparison.Ordinal))
            .ToArray();
        return new HumanInputRequestLifecycleStoreSnapshot(
            head,
            Array.AsReadOnly(requests.ToArray()),
            Array.AsReadOnly(operations));
    }

    private static HumanInputRequestLifecycleStoreSnapshot? RelatedSnapshot(
        HumanInputRequestStoreDocument document,
        HumanInputRequestLifecycleOperationEvidence operation)
        => operation.RelatedRequestId is { } related ? Snapshot(document, related) : null;

    private static HumanInputRequestLifecycleStoredOperation? FindOperation(HumanInputRequestStoreDocument document, string operationId)
    {
        var evidence = document.Operations.SingleOrDefault(value => string.Equals(value.OperationId, operationId, StringComparison.Ordinal));
        return evidence is null ? null : new HumanInputRequestLifecycleStoredOperation(evidence.TargetRequestId, evidence);
    }

    private static HumanInputRequestLifecycleStoreCommitResult CommitProjection(
        HumanInputRequestLifecycleStoreCommitStatus status,
        HumanInputRequestStoreDocument document,
        string operationId)
    {
        var operation = FindOperation(document, operationId);
        if (operation is null)
        {
            return CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Ambiguous);
        }

        return new HumanInputRequestLifecycleStoreCommitResult(
            status,
            document.Generation,
            operation,
            Snapshot(document, operation.RequestId),
            RelatedSnapshot(document, operation.Evidence));
    }

    private async Task<CapabilityCatalogPathSession> AcquireAsync(CancellationToken cancellationToken)
        => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.LockPath, createRoot: false, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The Human Input request workspace root is unavailable.");

    private static string WorkspaceIdentity(CapabilityCatalogPathSession session)
        => CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(
            "embodysense-human-input-requests-v1\n" + session.PhysicalIdentityMaterial);

    private async ValueTask ObserveAsync(HumanInputRequestPersistenceBoundary boundary, CancellationToken cancellationToken)
    {
        if (_options.DurableBoundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static HumanInputRequestStoreOptions ValidateOptions(HumanInputRequestStoreOptions options)
    {
        if (options.MaxRequests is < 1 or > HumanInputRequestLifecycleContractLimits.MaxRequestsPerStore
            || options.MaxRequestVersions is < 1 or > HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerStore
            || options.MaxOperations is < 1 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || options.MaxArtifactUtf8Bytes is < 1 or > HumanInputRequestLifecycleContractLimits.MaxStoreDocumentUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Human Input request store options must remain within schema-1 bounds.");
        }

        return options;
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented)
        => new(JsonSerializerDefaults.Web)
        {
            WriteIndented = writeIndented,
            MaxDepth = HumanInputRequestLifecycleContractLimits.MaxJsonDepth,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false),
                new AuthorityActorIdJsonConverter(),
                new AuthorityPurposeJsonConverter(),
                new AuthorityGrantIdJsonConverter(),
                new AuthorityGrantRevisionJsonConverter()
            }
        };

    private static bool MatchesCurrent(HumanInputRequestStoreDocument document, CapabilityCatalogTrustState trust)
        => document.Generation == trust.CurrentGeneration
            && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);

    private static bool MatchesPrevious(HumanInputRequestStoreDocument document, CapabilityCatalogTrustState trust)
        => document.Generation == trust.PreviousGeneration
            && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);

    private static void RequireExactAdvance(
        string workspaceIdentity,
        CapabilityCatalogTrustState previous,
        long candidateGeneration,
        string candidateContentDigest,
        CapabilityCatalogTrustState? advanced)
    {
        if (advanced is null
            || !string.Equals(advanced.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            || advanced.CurrentGeneration != candidateGeneration
            || !string.Equals(advanced.CurrentContentDigest, candidateContentDigest, StringComparison.Ordinal)
            || advanced.PreviousGeneration != previous.CurrentGeneration
            || !string.Equals(advanced.PreviousContentDigest, previous.CurrentContentDigest, StringComparison.Ordinal))
        {
            throw new IOException("The trust provider did not prove the exact Human Input request-store successor.");
        }
    }

    private static bool IsHash(string? value)
        => value is { Length: HumanInputRequestLifecycleContractLimits.Sha256HexCharacters }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static HumanInputRequestLifecycleStoreReadResult ReadResult(HumanInputRequestLifecycleStoreReadStatus status)
        => new(status, 0, null, null, null);

    private static HumanInputRequestLifecycleStoreCommitResult CommitResult(HumanInputRequestLifecycleStoreCommitStatus status)
        => new(status, 0, null, null, null);

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
