using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.HumanInput.Catalog;
using EmbodySense.Core.Application.HumanInput.Catalog.Models;
using EmbodySense.Core.Application.HumanInput.Lifecycle;
using EmbodySense.Core.Application.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Application.HumanInput.Responses;
using EmbodySense.Core.Application.HumanInput.Responses.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.HumanInput.Responses;
using EmbodySense.Core.Common.HumanInput.Responses.Models;
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
public sealed class HumanInputRequestStore : IHumanInputRequestCatalog, IHumanInputRequestLifecycleStore, IHumanInputResponseLifecycleStore
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

    async Task<HumanInputRequestCatalogPage> IHumanInputRequestCatalog.ListAsync(
        HumanInputRequestCatalogPageRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null
            || request.MaximumCount is < 1 or > HumanInputRequestCatalogLimits.MaxPageSize)
        {
            return CatalogPage(HumanInputRequestCatalogPageStatus.Invalid);
        }

        var callbackEntered = false;
        HumanInputRequestCatalogPage? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    completed = await ListCatalogCoreAsync(request, token).ConfigureAwait(false);
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
            return completed ?? CatalogPage(callbackEntered
                ? HumanInputRequestCatalogPageStatus.Ambiguous
                : HumanInputRequestCatalogPageStatus.Unavailable);
        }
    }

    async Task<HumanInputRequestCatalogReadResult> IHumanInputRequestCatalog.ReadAsync(
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!HumanInputIdentifier.IsValid(requestId))
        {
            return CatalogRead(HumanInputRequestCatalogReadStatus.Invalid);
        }

        HumanInputRequestCatalogReadResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    completed = await ReadCatalogCoreAsync(requestId, token).ConfigureAwait(false);
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
            return completed ?? CatalogRead(HumanInputRequestCatalogReadStatus.Unavailable);
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

    /// <inheritdoc />
    async Task<HumanInputResponseLifecycleStoreReadResult> IHumanInputResponseLifecycleStore.ReadAsync(
        HumanInputRequestReference request,
        CancellationToken cancellationToken)
    {
        if (!HumanInputRequestLifecycleValidator.ValidateReference(request).IsValid)
        {
            return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        }

        HumanInputResponseLifecycleStoreReadResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    completed = await ReadResponseCoreAsync(request, token).ConfigureAwait(false);
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
            return completed ?? ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    async Task<HumanInputResponseLifecycleStoreReadResult> IHumanInputResponseLifecycleStore.ReadForMutationAsync(
        string requestId,
        string operationId,
        string commandHash,
        CancellationToken cancellationToken)
    {
        if (!HumanInputIdentifier.IsValid(requestId)
            || !HumanInputIdentifier.IsValid(operationId, HumanInputRequestLifecycleContractLimits.MaxOperationIdCharacters)
            || !IsHash(commandHash))
        {
            return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        }

        var callbackEntered = false;
        HumanInputResponseLifecycleStoreReadResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    completed = await ReadResponseForMutationCoreAsync(
                        requestId,
                        operationId,
                        commandHash,
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
            return completed ?? ResponseReadResult(callbackEntered
                ? HumanInputResponseLifecycleStoreReadStatus.Ambiguous
                : HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        }
    }

    /// <inheritdoc />
    async Task<HumanInputResponseLifecycleStoreCommitResult> IHumanInputResponseLifecycleStore.CommitAsync(
        HumanInputResponseLifecycleStoreMutation mutation,
        CancellationToken cancellationToken)
    {
        if (!TryCaptureResponseMutation(mutation, out var captured))
        {
            return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Unavailable);
        }

        var callbackEntered = false;
        HumanInputResponseLifecycleStoreCommitResult? completed = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackEntered = true;
                    completed = await CommitResponseCoreAsync(captured!, token, cancellationToken).ConfigureAwait(false);
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
            return completed ?? ResponseCommitResult(callbackEntered
                ? HumanInputResponseLifecycleStoreCommitStatus.Ambiguous
                : HumanInputResponseLifecycleStoreCommitStatus.Unavailable);
        }
    }

    private async Task<HumanInputResponseLifecycleStoreReadResult> ReadResponseCoreAsync(
        HumanInputRequestReference request,
        CancellationToken cancellationToken)
    {
        await using var session = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var workspaceIdentity = WorkspaceIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken).ConfigureAwait(false);
        if (loaded.Disposition is HumanInputRequestLoadDisposition.Pending or HumanInputRequestLoadDisposition.Recovered)
        {
            return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Ambiguous);
        }

        if (loaded.Document is null)
        {
            return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        }

        var snapshot = ResponseSnapshot(loaded.Document, request);
        return new HumanInputResponseLifecycleStoreReadResult(
            snapshot is null ? HumanInputResponseLifecycleStoreReadStatus.NotFound : HumanInputResponseLifecycleStoreReadStatus.Ready,
            loaded.Document.Generation,
            snapshot,
            null);
    }

    private async Task<HumanInputResponseLifecycleStoreReadResult> ReadResponseForMutationCoreAsync(
        string requestId,
        string operationId,
        string commandHash,
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
                if (!PendingResponseMatches(loaded.Pending!, requestId, operationId, commandHash))
                {
                    return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Ambiguous);
                }

                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken).ConfigureAwait(false);
            }

            if (loaded.Disposition == HumanInputRequestLoadDisposition.Recovered)
            {
                return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Ambiguous);
            }

            if (loaded.Document is null)
            {
                return ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus.Unavailable);
            }

            var document = loaded.Document;
            var currentSnapshot = ResponseSnapshot(document, requestId);
            var envelope = FindOperationEnvelope(document, operationId);
            if (envelope is null)
            {
                return new HumanInputResponseLifecycleStoreReadResult(
                    currentSnapshot is null ? HumanInputResponseLifecycleStoreReadStatus.NotFound : HumanInputResponseLifecycleStoreReadStatus.Ready,
                    document.Generation,
                    currentSnapshot,
                    null);
            }

            var operation = ResponseOperation(envelope);
            var exact = operation is not null
                && string.Equals(operation.RequestId, requestId, StringComparison.Ordinal)
                && FixedHashEquals(operation.Evidence.CommandHash, commandHash);
            var exactSnapshot = exact
                ? ReplayResponseSnapshot(document, operation!.Evidence)
                : currentSnapshot;
            return new HumanInputResponseLifecycleStoreReadResult(
                exact
                    ? exactSnapshot is null ? HumanInputResponseLifecycleStoreReadStatus.NotFound : HumanInputResponseLifecycleStoreReadStatus.Ready
                    : HumanInputResponseLifecycleStoreReadStatus.OperationConflict,
                document.Generation,
                exactSnapshot,
                exact ? operation : null);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return ResponseReadResult(outcomeMayHaveCommitted
                ? HumanInputResponseLifecycleStoreReadStatus.Ambiguous
                : HumanInputResponseLifecycleStoreReadStatus.Unavailable);
        }
    }

    private async Task<HumanInputResponseLifecycleStoreCommitResult> CommitResponseCoreAsync(
        HumanInputResponseLifecycleStoreMutation mutation,
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
                var pending = loaded.Pending!;
                if (!PendingResponseMatches(
                        pending,
                        mutation.Operation.Request.RequestId,
                        mutation.Operation.OperationId,
                        mutation.Operation.CommandHash))
                {
                    return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Ambiguous);
                }

                var evidenceMatches = ResponseEvidenceEquals(pending.Operations[^1].ResponseLifecycle!, mutation.Operation);
                outcomeMayHaveCommitted = true;
                loaded = await FinalizePendingAsync(workspaceIdentity, trust!, loaded, cancellationToken).ConfigureAwait(false);
                return ResponseCommitProjection(
                    evidenceMatches
                        ? HumanInputResponseLifecycleStoreCommitStatus.Replayed
                        : HumanInputResponseLifecycleStoreCommitStatus.OperationConflict,
                    loaded.Document!,
                    mutation.Operation.OperationId);
            }

            if (loaded.Disposition == HumanInputRequestLoadDisposition.Recovered)
            {
                return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Ambiguous);
            }

            if (loaded.Document is null)
            {
                return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Unavailable);
            }

            var current = loaded.Document;
            var existingEnvelope = FindOperationEnvelope(current, mutation.Operation.OperationId);
            if (existingEnvelope is not null)
            {
                var existing = ResponseOperation(existingEnvelope);
                if (existing is not null && ResponseEvidenceEquals(existing.Evidence, mutation.Operation))
                {
                    return ResponseCommitProjection(HumanInputResponseLifecycleStoreCommitStatus.Replayed, current, mutation.Operation.OperationId);
                }

                var sameIntent = existing is not null
                    && string.Equals(existing.RequestId, mutation.Operation.Request.RequestId, StringComparison.Ordinal)
                    && FixedHashEquals(existing.Evidence.CommandHash, mutation.Operation.CommandHash);
                return sameIntent
                    ? ResponseCommitProjection(HumanInputResponseLifecycleStoreCommitStatus.OperationConflict, current, mutation.Operation.OperationId)
                    : new HumanInputResponseLifecycleStoreCommitResult(
                        HumanInputResponseLifecycleStoreCommitStatus.OperationConflict,
                        current.Generation,
                        null,
                        ResponseSnapshot(current, mutation.Operation.Request.RequestId));
            }

            if (mutation.ExpectedStoreGeneration != current.Generation)
            {
                return new HumanInputResponseLifecycleStoreCommitResult(
                    HumanInputResponseLifecycleStoreCommitStatus.StoreConflict,
                    current.Generation,
                    null,
                    ResponseSnapshot(current, mutation.Operation.Request.RequestId));
            }

            if (WouldExceedResponseCountLimit(current, mutation))
            {
                return new HumanInputResponseLifecycleStoreCommitResult(
                    HumanInputResponseLifecycleStoreCommitStatus.LimitExceeded,
                    current.Generation,
                    null,
                    ResponseSnapshot(current, mutation.Operation.Request.RequestId));
            }

            if (!TryCreateResponseCandidate(current, mutation, workspaceIdentity, out var candidate)
                || candidate is null)
            {
                return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Unavailable);
            }

            if (!HumanInputRequestStoreStateValidator.Validate(candidate, workspaceIdentity, _options)
                || !HumanInputRequestStoreStateValidator.IsDirectSuccessor(current, candidate))
            {
                return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Unavailable);
            }

            if (WouldExceedArtifactLimit(candidate))
            {
                return new HumanInputResponseLifecycleStoreCommitResult(
                    HumanInputResponseLifecycleStoreCommitStatus.LimitExceeded,
                    current.Generation,
                    null,
                    ResponseSnapshot(current, mutation.Operation.Request.RequestId));
            }

            var committed = await PublishCandidateAsync(
                session,
                workspaceIdentity,
                trust,
                current,
                candidate,
                () => outcomeMayHaveCommitted = true,
                cancellationToken).ConfigureAwait(false);
            return committed is null
                ? ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Unavailable)
                : ResponseCommitProjection(HumanInputResponseLifecycleStoreCommitStatus.Committed, committed, mutation.Operation.OperationId);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested && !outcomeMayHaveCommitted)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return ResponseCommitResult(outcomeMayHaveCommitted
                ? HumanInputResponseLifecycleStoreCommitStatus.Ambiguous
                : HumanInputResponseLifecycleStoreCommitStatus.Unavailable);
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

    private async Task<HumanInputRequestCatalogPage> ListCatalogCoreAsync(
        HumanInputRequestCatalogPageRequest request,
        CancellationToken cancellationToken)
    {
        await using var session = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var workspaceIdentity = WorkspaceIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken).ConfigureAwait(false);
        if (loaded.Disposition is HumanInputRequestLoadDisposition.Pending or HumanInputRequestLoadDisposition.Recovered)
        {
            return CatalogPage(HumanInputRequestCatalogPageStatus.Ambiguous);
        }

        if (loaded.Document is null)
        {
            return CatalogPage(HumanInputRequestCatalogPageStatus.Unavailable);
        }

        var document = loaded.Document;
        string? startAfterRequestId = null;
        if (request.Cursor is not null)
        {
            if (!HumanInputRequestCatalogCursor.TryParse(request.Cursor, out var cursor))
            {
                return CatalogPage(HumanInputRequestCatalogPageStatus.Invalid);
            }

            if (cursor.Generation != document.Generation
                || !string.Equals(cursor.ContentDigest, document.ContentDigest, StringComparison.Ordinal))
            {
                return CatalogPage(HumanInputRequestCatalogPageStatus.Stale, document.Generation);
            }

            if (!document.Heads.Any(head => string.Equals(head.RequestId, cursor.LastRequestId, StringComparison.Ordinal)))
            {
                return CatalogPage(HumanInputRequestCatalogPageStatus.Ambiguous, document.Generation);
            }

            startAfterRequestId = cursor.LastRequestId;
        }

        var heads = document.Heads
            .OrderBy(head => head.RequestId, StringComparer.Ordinal)
            .ToArray();
        var candidates = startAfterRequestId is null
            ? heads
            : heads.Where(head => string.CompareOrdinal(head.RequestId, startAfterRequestId) > 0).ToArray();
        var pageHeads = candidates.Take(request.MaximumCount).ToArray();
        var entries = new List<HumanInputRequestCatalogEntry>(pageHeads.Length);
        foreach (var head in pageHeads)
        {
            var entry = CatalogEntry(document, head);
            if (entry is null)
            {
                return CatalogPage(HumanInputRequestCatalogPageStatus.Ambiguous, document.Generation);
            }

            entries.Add(entry);
        }

        var hasMore = candidates.Length > pageHeads.Length;
        var nextCursor = hasMore
            ? HumanInputRequestCatalogCursor.Create(document.Generation, document.ContentDigest, pageHeads[^1].RequestId)
            : null;
        return new HumanInputRequestCatalogPage(
            HumanInputRequestCatalogPageStatus.Ready,
            document.Generation,
            Array.AsReadOnly(entries.ToArray()),
            nextCursor);
    }

    private async Task<HumanInputRequestCatalogReadResult> ReadCatalogCoreAsync(string requestId, CancellationToken cancellationToken)
    {
        await using var session = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        var workspaceIdentity = WorkspaceIdentity(session);
        var trust = await _trustProvider.ReadAsync(workspaceIdentity, cancellationToken).ConfigureAwait(false);
        var loaded = await LoadAsync(session, workspaceIdentity, trust, cancellationToken).ConfigureAwait(false);
        if (loaded.Disposition is HumanInputRequestLoadDisposition.Pending or HumanInputRequestLoadDisposition.Recovered)
        {
            return CatalogRead(HumanInputRequestCatalogReadStatus.Ambiguous);
        }

        if (loaded.Document is null)
        {
            return CatalogRead(HumanInputRequestCatalogReadStatus.Unavailable);
        }

        var head = loaded.Document.Heads.SingleOrDefault(value => string.Equals(value.RequestId, requestId, StringComparison.Ordinal));
        if (head is null)
        {
            return new HumanInputRequestCatalogReadResult(HumanInputRequestCatalogReadStatus.NotFound, loaded.Document.Generation, null);
        }

        var entry = CatalogEntry(loaded.Document, head);
        return new HumanInputRequestCatalogReadResult(
            entry is null ? HumanInputRequestCatalogReadStatus.Ambiguous : HumanInputRequestCatalogReadStatus.Ready,
            loaded.Document.Generation,
            entry);
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
            var envelope = FindOperationEnvelope(document, operationId);
            if (envelope is null)
            {
                return new HumanInputRequestLifecycleStoreReadResult(
                    primary is null ? HumanInputRequestLifecycleStoreReadStatus.NotFound : HumanInputRequestLifecycleStoreReadStatus.Ready,
                    document.Generation,
                    primary,
                    requestedRelated,
                    null);
            }

            var operation = LifecycleOperation(envelope);
            var exact = operation is not null
                && string.Equals(operation.RequestId, requestId, StringComparison.Ordinal)
                && string.Equals(operation.Evidence.RequestHash, requestHash, StringComparison.Ordinal)
                && (relatedRequestId is null
                    || string.Equals(operation.Evidence.RelatedRequestId, relatedRequestId, StringComparison.Ordinal));
            var related = exact && operation!.Evidence.RelatedRequestId is { } relatedId
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
            var existingEnvelope = FindOperationEnvelope(current, mutation.Operation.OperationId);
            if (existingEnvelope is not null)
            {
                var existing = LifecycleOperation(existingEnvelope);
                return existing is not null && Equals(existing.Evidence, mutation.Operation)
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

            var committed = await PublishCandidateAsync(
                session,
                workspaceIdentity,
                trust,
                current,
                candidate,
                () => outcomeMayHaveCommitted = true,
                cancellationToken).ConfigureAwait(false);
            return committed is null
                ? CommitResult(HumanInputRequestLifecycleStoreCommitStatus.Unavailable)
                : CommitProjection(HumanInputRequestLifecycleStoreCommitStatus.Committed, committed, mutation.Operation.OperationId);
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

    private async Task<HumanInputRequestStoreDocument?> PublishCandidateAsync(
        CapabilityCatalogPathSession session,
        string workspaceIdentity,
        CapabilityCatalogTrustState? trust,
        HumanInputRequestStoreDocument current,
        HumanInputRequestStoreDocument candidate,
        Action durableIntentBegan,
        CancellationToken cancellationToken)
    {
        var currentDigest = ComputeContentDigest(current).Value;
        if (trust is null)
        {
            trust = await _trustProvider.InitializeAsync(workspaceIdentity, current.Generation, currentDigest, cancellationToken).ConfigureAwait(false);
            await ObserveAsync(HumanInputRequestPersistenceBoundary.TrustInitialized, cancellationToken).ConfigureAwait(false);
        }

        if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
        {
            return null;
        }

        var proof = await SerializeAsync(workspaceIdentity, current, cancellationToken).ConfigureAwait(false);
        var serializedCandidate = await SerializeAsync(workspaceIdentity, candidate, cancellationToken).ConfigureAwait(false);
        await session.WriteTextAtomicallyAsync(_paths.ProofPath, proof.Json, cancellationToken).ConfigureAwait(false);
        await ObserveAsync(HumanInputRequestPersistenceBoundary.ProofPublished, cancellationToken).ConfigureAwait(false);
        durableIntentBegan();
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
        return candidate with
        {
            ContentDigest = serializedCandidate.ContentDigest,
            AuthenticationTag = serializedCandidate.AuthenticationTag
        };
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
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new HumanInputRequestLoadResult(primary, null, HumanInputRequestLoadDisposition.Current);
        }

        var proof = proofExists
            ? await TryReadAsync(session, workspaceIdentity, _paths.ProofPath, cancellationToken).ConfigureAwait(false)
            : null;
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
                    _options.MaxResponseArtifacts,
                    _options.MaxSelections,
                    _options.MaxOperations))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<HumanInputRequestStoreDocument>(text, _jsonOptions);
            if (!HasSafeAuthenticationEnvelope(document, workspaceIdentity)
                || string.IsNullOrEmpty(document!.AuthenticationTag)
                || _strictUtf8.GetByteCount(document.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes
                || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var digest, out _)
                || !TryComputeContentDigest(document, out var computedDigest)
                || !digest!.FixedTimeEquals(computedDigest)
                || !await _trustProvider.VerifyArtifactAsync(
                    workspaceIdentity,
                    document.Generation,
                    document.ContentDigest,
                    document.AuthenticationTag,
                    cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (_options.AuthenticatedArtifactObserver is { } observer)
            {
                await observer(cancellationToken).ConfigureAwait(false);
            }
            if (!HumanInputRequestStoreStateValidator.Validate(document, workspaceIdentity, _options))
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

    private bool HasSafeAuthenticationEnvelope(
        HumanInputRequestStoreDocument? document,
        string workspaceIdentity)
        => document is not null
            && document.SchemaVersion == HumanInputRequestStoreDocument.CurrentSchemaVersion
            && string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
            && document.Generation is >= 0 and <= HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            && document.RequestVersions is not null
            && document.Heads is not null
            && document.ResponseArtifacts is not null
            && document.Selections is not null
            && document.Operations is not null
            && document.Generation == document.Operations.Count
            && document.RequestVersions.Count <= _options.MaxRequestVersions
            && document.Heads.Count <= _options.MaxRequests
            && document.ResponseArtifacts.Count <= _options.MaxResponseArtifacts
            && document.Selections.Count <= _options.MaxSelections
            && document.Operations.Count <= _options.MaxOperations;

    private static bool TryComputeContentDigest(
        HumanInputRequestStoreDocument document,
        out CapabilityIntegrityDigest? digest)
    {
        try
        {
            digest = ComputeContentDigest(document);
            return true;
        }
        catch (JsonException)
        {
            digest = null;
            return false;
        }
        catch (NotSupportedException)
        {
            digest = null;
            return false;
        }
        catch (InvalidOperationException)
        {
            digest = null;
            return false;
        }
        catch (ArgumentException)
        {
            digest = null;
            return false;
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
            current.ResponseArtifacts.ToArray(),
            current.Selections.ToArray(),
            current.Operations.Append(HumanInputRequestStoreOperationDocument.From(mutation.Operation)).ToArray(),
            string.Empty,
            string.Empty);
    }

    private static bool TryCreateResponseCandidate(
        HumanInputRequestStoreDocument current,
        HumanInputResponseLifecycleStoreMutation mutation,
        string workspaceIdentity,
        out HumanInputRequestStoreDocument? candidate)
    {
        candidate = null;
        var request = FindRequest(current, mutation.Operation.Request);
        if (request is null
            && (mutation.ResponseToAppend is not null || mutation.SelectionToAppend is not null || mutation.RequestHeadToWrite is not null))
        {
            return false;
        }

        HumanInputResponseArtifact? response = null;
        if (mutation.ResponseToAppend is not null
            && (request is null
                || !HumanInputResponseArtifactSnapshot.TryCapture(request, mutation.ResponseToAppend, out response, out _)
                || response is null
                || mutation.Operation.SubmittedResponse is null
                || !mutation.Operation.SubmittedResponse.Matches(request, response)))
        {
            return false;
        }

        if (response is not null
            && current.ResponseArtifacts.Any(existing =>
                Equals(existing.Request, response.Request)
                && string.Equals(existing.ResponseId, response.ResponseId, StringComparison.Ordinal)))
        {
            return false;
        }

        var responses = response is null
            ? current.ResponseArtifacts.ToArray()
            : current.ResponseArtifacts.Append(response).ToArray();
        var active = request is null ? [] : ActiveResponses(current, request).ToList();
        if (response is not null)
        {
            active.Add(response);
        }

        HumanInputResponseSelection? selection = null;
        if (mutation.SelectionToAppend is not null
            && (request is null
                || !HumanInputResponseSelectionSnapshot.TryCapture(request, mutation.SelectionToAppend, active, out selection, out _)
                || selection is null
                || mutation.Operation.Selection is null
                || !mutation.Operation.Selection.Matches(selection)))
        {
            return false;
        }

        if (selection is not null
            && current.Selections.Any(existing =>
                Equals(existing.Request, selection.Request)
                || string.Equals(existing.SelectionId, selection.SelectionId, StringComparison.Ordinal)))
        {
            return false;
        }

        var heads = current.Heads.ToArray();
        if (mutation.RequestHeadToWrite is { } head)
        {
            heads = current.Heads
                .Where(existing => !string.Equals(existing.RequestId, head.RequestId, StringComparison.Ordinal))
                .Append(head)
                .OrderBy(existing => existing.RequestId, StringComparer.Ordinal)
                .ToArray();
        }

        candidate = new HumanInputRequestStoreDocument(
            HumanInputRequestStoreDocument.CurrentSchemaVersion,
            workspaceIdentity,
            checked(current.Generation + 1),
            current.RequestVersions.ToArray(),
            heads,
            responses,
            selection is null ? current.Selections.ToArray() : current.Selections.Append(selection).ToArray(),
            current.Operations.Append(HumanInputRequestStoreOperationDocument.From(mutation.Operation)).ToArray(),
            string.Empty,
            string.Empty);
        return true;
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

    private static bool TryCaptureResponseMutation(
        HumanInputResponseLifecycleStoreMutation? mutation,
        out HumanInputResponseLifecycleStoreMutation? captured)
    {
        captured = null;
        if (mutation is null
            || mutation.ExpectedStoreGeneration is < 0 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerStore
            || !HumanInputResponseContractValidator.ValidateEvidence(mutation.Operation).IsValid
            || !HumanInputResponseEligibilityEvidenceHash.Matches(mutation.Operation))
        {
            return false;
        }

        if (!HumanInputResponseOperationEvidenceSnapshot.TryCapture(
                mutation.Operation,
                out var operation,
                out _)
            || operation is null)
        {
            return false;
        }
        var committed = operation.Outcome == HumanInputResponseOperationOutcome.Committed;
        var appendsResponse = committed && operation.Kind == HumanInputResponseOperationKind.Submit;
        var appendsSelection = committed && operation.Selection is not null;
        HumanInputResponseArtifact? response = null;
        HumanInputResponseSelection? selection = null;
        if (mutation.ResponseToAppend is not null
            && !TryCaptureResponseArtifact(mutation.ResponseToAppend, out response)
            || mutation.SelectionToAppend is not null
                && !TryCaptureResponseSelection(mutation.SelectionToAppend, out selection)
            || appendsResponse != (response is not null)
            || appendsSelection != (selection is not null)
            || appendsSelection != (mutation.RequestHeadToWrite is not null)
            || mutation.RequestHeadToWrite is not null && !Equals(mutation.RequestHeadToWrite, operation.ResultHead)
            || response is not null && operation.SubmittedResponse is null
            || selection is not null
                && (operation.Selection is null || !operation.Selection.Matches(selection)))
        {
            return false;
        }

        captured = mutation with
        {
            Operation = operation,
            ResponseToAppend = response,
            SelectionToAppend = selection,
            RequestHeadToWrite = mutation.RequestHeadToWrite is null ? null : operation.ResultHead
        };
        return true;
    }

    private static bool TryCaptureResponseArtifact(
        HumanInputResponseArtifact artifact,
        out HumanInputResponseArtifact? snapshot)
    {
        snapshot = null;
        try
        {
            _ = HumanInputResponseArtifactHash.Compute(artifact);
            var value = artifact.Value;
            ImmutableArray<HumanInputStructuredFieldValue>? fields = value.StructuredFields is not { } source
                ? null
                : source.Select(field => field is null ? null! : field with { }).ToImmutableArray();
            snapshot = artifact with
            {
                Request = artifact.Request with { },
                Binding = artifact.Binding with { },
                Value = value with
                {
                    StructuredFields = fields,
                    Reference = value.Reference is null ? null : value.Reference with { }
                }
            };
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or IndexOutOfRangeException
            or NullReferenceException)
        {
            return false;
        }
    }

    private static bool TryCaptureResponseSelection(
        HumanInputResponseSelection selection,
        out HumanInputResponseSelection? snapshot)
    {
        snapshot = null;
        try
        {
            _ = HumanInputResponseSelectionHash.Compute(selection);
            snapshot = selection with
            {
                Request = selection.Request with { },
                Responses = selection.Responses.Select(reference => reference is null
                    ? null!
                    : reference with { Request = reference.Request with { } }).ToImmutableArray()
            };
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or IndexOutOfRangeException
            or NullReferenceException)
        {
            return false;
        }
    }

    private bool WouldExceedResponseCountLimit(
        HumanInputRequestStoreDocument current,
        HumanInputResponseLifecycleStoreMutation mutation)
    {
        if (current.Operations.Count >= _options.MaxOperations
            || ClaimedResponseOperations(current, mutation.Operation.Request).Count()
                >= _options.MaxResponseOperationsPerRequest)
        {
            return true;
        }

        if (mutation.ResponseToAppend is not null
            && (current.ResponseArtifacts.Count >= _options.MaxResponseArtifacts
                || current.ResponseArtifacts.Count(response => Equals(response.Request, mutation.Operation.Request))
                    >= HumanInputResponseContractLimits.MaxResponsesPerRequest))
        {
            return true;
        }

        return mutation.SelectionToAppend is not null
            && (current.Selections.Count >= _options.MaxSelections
                || current.Selections.Any(selection => Equals(selection.Request, mutation.Operation.Request)));
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

        var targetOperations = current.Operations.Count(operation => LifecycleOperationTouchesRequest(operation, mutation.Operation.TargetRequestId));
        if (targetOperations >= _options.MaxLifecycleOperationsPerRequest)
        {
            return true;
        }

        if (mutation.Operation.RelatedRequestId is { } related)
        {
            var relatedOperations = current.Operations.Count(operation => LifecycleOperationTouchesRequest(operation, related));
            if (relatedOperations >= _options.MaxLifecycleOperationsPerRequest)
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
        var operation = pending.Operations[^1].RequestLifecycle;
        return pending.Operations[^1].Family == HumanInputRequestStoreOperationFamily.RequestLifecycle
            && operation is not null
            && string.Equals(operation.TargetRequestId, requestId, StringComparison.Ordinal)
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
        if (operation.Family != HumanInputRequestStoreOperationFamily.RequestLifecycle
            || !Equals(operation.RequestLifecycle, mutation.Operation))
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

    private static bool PendingResponseMatches(
        HumanInputRequestStoreDocument pending,
        string requestId,
        string operationId,
        string commandHash)
    {
        var envelope = pending.Operations[^1];
        var operation = envelope.ResponseLifecycle;
        return envelope.Family == HumanInputRequestStoreOperationFamily.ResponseLifecycle
            && operation is not null
            && string.Equals(operation.Request.RequestId, requestId, StringComparison.Ordinal)
            && string.Equals(operation.OperationId, operationId, StringComparison.Ordinal)
            && FixedHashEquals(operation.CommandHash, commandHash);
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
            .Select(operation => operation.RequestLifecycle)
            .Where(operation => operation is not null
                && (string.Equals(operation.TargetRequestId, requestId, StringComparison.Ordinal)
                    || string.Equals(operation.RelatedRequestId, requestId, StringComparison.Ordinal)))
            .Cast<HumanInputRequestLifecycleOperationEvidence>()
            .ToArray();
        var answerOperation = document.Operations
            .Select(operation => operation.ResponseLifecycle)
            .SingleOrDefault(operation => operation is not null
                && string.Equals(operation.Request.RequestId, requestId, StringComparison.Ordinal)
                && operation.Selection is not null
                && Equals(operation.ResultHead, head));
        return new HumanInputRequestLifecycleStoreSnapshot(
            head,
            Array.AsReadOnly(requests.ToArray()),
            Array.AsReadOnly(operations),
            answerOperation);
    }

    private static HumanInputRequestCatalogEntry? CatalogEntry(
        HumanInputRequestStoreDocument document,
        HumanInputRequestLifecycleHead head)
    {
        var lifecycle = Snapshot(document, head.RequestId);
        var responses = ResponseSnapshot(document, head.CurrentRequest);
        return lifecycle is null || responses is null ? null : new HumanInputRequestCatalogEntry(lifecycle, responses);
    }

    private static HumanInputRequestLifecycleStoreSnapshot? RelatedSnapshot(
        HumanInputRequestStoreDocument document,
        HumanInputRequestLifecycleOperationEvidence operation)
        => operation.RelatedRequestId is { } related ? Snapshot(document, related) : null;

    internal static HumanInputResponseLifecycleStoreSnapshot? ResponseSnapshot(
        HumanInputRequestStoreDocument document,
        string requestId)
    {
        var head = document.Heads.SingleOrDefault(value => string.Equals(value.RequestId, requestId, StringComparison.Ordinal));
        return head is null ? null : ResponseSnapshot(document, head.CurrentRequest);
    }

    internal static HumanInputResponseLifecycleStoreSnapshot? ResponseSnapshot(
        HumanInputRequestStoreDocument document,
        HumanInputRequestReference requestReference)
    {
        var requestSnapshot = Snapshot(document, requestReference.RequestId);
        if (requestSnapshot is null
            || FindRequest(document, requestReference) is not { } request)
        {
            return null;
        }

        var responses = new List<HumanInputResponseArtifact>();
        foreach (var source in document.ResponseArtifacts.Where(value => Equals(value.Request, requestReference)))
        {
            if (!HumanInputResponseArtifactSnapshot.TryCapture(request, source, out var captured, out _) || captured is null)
            {
                return null;
            }
            responses.Add(captured);
        }

        var operations = ClaimedResponseOperations(document, requestReference)
            .Select(CaptureResponseEvidence)
            .ToArray();
        var active = ActiveResponses(document, request);
        var sourceSelection = document.Selections.SingleOrDefault(value => Equals(value.Request, requestReference));
        HumanInputResponseSelection? selection = null;
        if (sourceSelection is not null
            && (!HumanInputResponseSelectionSnapshot.TryCapture(request, sourceSelection, active, out selection, out _)
                || selection is null))
        {
            return null;
        }

        return new HumanInputResponseLifecycleStoreSnapshot(
            requestSnapshot,
            requestReference,
            Array.AsReadOnly(responses.ToArray()),
            Array.AsReadOnly(operations),
            selection);
    }

    private static IReadOnlyList<HumanInputResponseArtifact> ActiveResponses(
        HumanInputRequestStoreDocument document,
        HumanInputRequest request)
    {
        var retained = document.ResponseArtifacts
            .Where(artifact => artifact.Request.Matches(request))
            .ToDictionary(artifact => artifact.ResponseId, StringComparer.Ordinal);
        var active = new List<HumanInputResponseArtifact>();
        foreach (var operation in document.Operations.Select(value => value.ResponseLifecycle))
        {
            if (operation is null
                || !operation.Request.Matches(request)
                || operation.Outcome != HumanInputResponseOperationOutcome.Committed)
            {
                continue;
            }

            if (operation.Kind == HumanInputResponseOperationKind.Submit
                && operation.SubmittedResponse is { } submitted
                && retained.TryGetValue(submitted.ResponseId, out var artifact)
                && submitted.Matches(request, artifact)
                && active.All(value => !string.Equals(value.ResponseId, artifact.ResponseId, StringComparison.Ordinal)))
            {
                active.Add(artifact);
            }
            else if (operation.Kind == HumanInputResponseOperationKind.Withdraw
                && operation.TargetResponses.Length == 1)
            {
                active.RemoveAll(value => string.Equals(value.ResponseId, operation.TargetResponses[0].ResponseId, StringComparison.Ordinal));
            }
        }

        return Array.AsReadOnly(active.ToArray());
    }

    private static HumanInputRequest? FindRequest(
        HumanInputRequestStoreDocument document,
        HumanInputRequestReference reference)
        => document.RequestVersions.SingleOrDefault(request => reference.Matches(request));

    private static HumanInputRequestStoreOperationDocument? FindOperationEnvelope(HumanInputRequestStoreDocument document, string operationId)
        => document.Operations.SingleOrDefault(value => string.Equals(value.OperationId, operationId, StringComparison.Ordinal));

    private static HumanInputRequestLifecycleStoredOperation? LifecycleOperation(HumanInputRequestStoreOperationDocument envelope)
        => envelope.RequestLifecycle is { } evidence
            ? new HumanInputRequestLifecycleStoredOperation(evidence.TargetRequestId, evidence)
            : null;

    private static HumanInputResponseLifecycleStoredOperation? ResponseOperation(HumanInputRequestStoreOperationDocument envelope)
        => envelope.ResponseLifecycle is { } evidence
            ? new HumanInputResponseLifecycleStoredOperation(evidence.Request.RequestId, CaptureResponseEvidence(evidence))
            : null;

    private static HumanInputResponseOperationEvidence CaptureResponseEvidence(HumanInputResponseOperationEvidence evidence)
        => HumanInputResponseOperationEvidenceSnapshot.TryCapture(evidence, out var snapshot, out _)
            && snapshot is not null
                ? snapshot
                : throw new InvalidOperationException("Authenticated response evidence could not be captured.");

    private static bool ResponseEvidenceEquals(
        HumanInputResponseOperationEvidence left,
        HumanInputResponseOperationEvidence right)
        => Equals(
                left with { AttemptedResponse = null, TargetResponses = default },
                right with { AttemptedResponse = null, TargetResponses = default })
            && ResponseArtifactEquals(left.AttemptedResponse, right.AttemptedResponse)
            && !left.TargetResponses.IsDefault
            && !right.TargetResponses.IsDefault
            && left.TargetResponses.SequenceEqual(right.TargetResponses);

    private static bool ResponseArtifactEquals(
        HumanInputResponseArtifact? left,
        HumanInputResponseArtifact? right)
        => left is null || right is null
            ? left is null && right is null
            : left.SchemaVersion == right.SchemaVersion
                && string.Equals(left.ResponseId, right.ResponseId, StringComparison.Ordinal)
                && Equals(left.Request, right.Request)
                && Equals(left.Binding, right.Binding)
                && left.ActorId.Equals(right.ActorId)
                && string.Equals(left.RespondentRoleId, right.RespondentRoleId, StringComparison.Ordinal)
                && left.SubmittedAtUtc == right.SubmittedAtUtc
                && left.PrivacyClass == right.PrivacyClass
                && ResponseValueEquals(left.Value, right.Value)
                && string.Equals(left.Explanation, right.Explanation, StringComparison.Ordinal)
                && string.Equals(left.ValueHash, right.ValueHash, StringComparison.Ordinal)
                && string.Equals(left.ResponseHash, right.ResponseHash, StringComparison.Ordinal);

    private static bool ResponseValueEquals(HumanInputResponseValue left, HumanInputResponseValue right)
        => left.Kind == right.Kind
            && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
            && string.Equals(left.ChoiceId, right.ChoiceId, StringComparison.Ordinal)
            && left.Confirmation == right.Confirmation
            && NullableSequenceEqual(left.StructuredFields, right.StructuredFields)
            && Equals(left.Reference, right.Reference);

    private static bool NullableSequenceEqual<T>(
        ImmutableArray<T>? left,
        ImmutableArray<T>? right)
        => left.HasValue == right.HasValue
            && (!left.HasValue
                || !left.Value.IsDefault
                    && !right!.Value.IsDefault
                    && left.Value.SequenceEqual(right.Value));

    private static HumanInputRequestLifecycleStoreCommitResult CommitProjection(
        HumanInputRequestLifecycleStoreCommitStatus status,
        HumanInputRequestStoreDocument document,
        string operationId)
    {
        var envelope = FindOperationEnvelope(document, operationId);
        var operation = envelope is null ? null : LifecycleOperation(envelope);
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

    private static HumanInputResponseLifecycleStoreCommitResult ResponseCommitProjection(
        HumanInputResponseLifecycleStoreCommitStatus status,
        HumanInputRequestStoreDocument document,
        string operationId)
    {
        var envelope = FindOperationEnvelope(document, operationId);
        var operation = envelope is null ? null : ResponseOperation(envelope);
        if (operation is null)
        {
            return ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus.Ambiguous);
        }

        return new HumanInputResponseLifecycleStoreCommitResult(
            status,
            document.Generation,
            operation,
            ReplayResponseSnapshot(document, operation.Evidence));
    }

    internal static HumanInputResponseLifecycleStoreSnapshot? ReplayResponseSnapshot(
        HumanInputRequestStoreDocument document,
        HumanInputResponseOperationEvidence operation)
    {
        if (operation.FailureCode == HumanInputResponseOperationFailureCode.RequestNotFound)
        {
            return null;
        }

        return ResponseSnapshot(document, operation.Request)
            ?? (operation.FailureCode is HumanInputResponseOperationFailureCode.StaleResponse
                    or HumanInputResponseOperationFailureCode.RequestTerminal
                ? ResponseSnapshot(document, operation.Request.RequestId)
                : null);
    }

    private static IEnumerable<HumanInputResponseOperationEvidence> ClaimedResponseOperations(
        HumanInputRequestStoreDocument document,
        HumanInputRequestReference request)
    {
        var claimed = new HashSet<HumanInputRequestReference>();
        foreach (var envelope in document.Operations)
        {
            if (envelope.RequestLifecycle is
                {
                    Outcome: HumanInputRequestLifecycleOperationOutcome.Committed,
                    CandidateRequest: { } candidate
                })
            {
                claimed.Add(candidate);
            }
            else if (envelope.ResponseLifecycle is { } response
                && claimed.Contains(response.Request)
                && Equals(response.Request, request))
            {
                yield return response;
            }
        }
    }

    private static bool LifecycleOperationTouchesRequest(HumanInputRequestStoreOperationDocument operation, string requestId)
        => operation.RequestLifecycle is { } lifecycle
            && (string.Equals(lifecycle.TargetRequestId, requestId, StringComparison.Ordinal)
                || string.Equals(lifecycle.RelatedRequestId, requestId, StringComparison.Ordinal));

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
            || options.MaxLifecycleOperationsPerRequest is < 1 or > HumanInputRequestLifecycleContractLimits.MaxOperationsPerRequest
            || options.MaxResponseOperationsPerRequest is < 1 or > HumanInputResponseContractLimits.MaxOperationsPerRequest
            || options.MaxResponseArtifacts is < 1 or > HumanInputRequestLifecycleContractLimits.MaxRequestVersionsPerStore
            || options.MaxSelections is < 1 or > HumanInputRequestLifecycleContractLimits.MaxRequestsPerStore
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

    private static bool FixedHashEquals(string? left, string? right)
        => IsHash(left)
            && IsHash(right)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(left!),
                Encoding.ASCII.GetBytes(right!));

    private static bool HasUtf8Bom(IReadOnlyList<byte> bytes)
        => bytes.Count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static HumanInputRequestLifecycleStoreReadResult ReadResult(HumanInputRequestLifecycleStoreReadStatus status)
        => new(status, 0, null, null, null);

    private static HumanInputRequestCatalogPage CatalogPage(
        HumanInputRequestCatalogPageStatus status,
        long storeGeneration = 0)
        => new(status, storeGeneration, [], null);

    private static HumanInputRequestCatalogReadResult CatalogRead(
        HumanInputRequestCatalogReadStatus status,
        long storeGeneration = 0)
        => new(status, storeGeneration, null);

    private static HumanInputRequestLifecycleStoreCommitResult CommitResult(HumanInputRequestLifecycleStoreCommitStatus status)
        => new(status, 0, null, null, null);

    private static HumanInputResponseLifecycleStoreReadResult ResponseReadResult(HumanInputResponseLifecycleStoreReadStatus status)
        => new(status, 0, null, null);

    private static HumanInputResponseLifecycleStoreCommitResult ResponseCommitResult(HumanInputResponseLifecycleStoreCommitStatus status)
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
