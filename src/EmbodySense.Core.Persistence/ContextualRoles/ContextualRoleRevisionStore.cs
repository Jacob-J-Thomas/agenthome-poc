using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.ContextualRoles;
using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles;
using EmbodySense.Core.Common.ContextualRoles.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.ContextualRoles.Models;

namespace EmbodySense.Core.Persistence.ContextualRoles;

/// <summary>Persists immutable contextual-role revisions and restart-safe lifecycle evidence within one guarded physical workspace.</summary>
/// <remarks>
/// The store publishes one immutable operation intent before any role mutation, then an immutable revision when required,
/// an atomic current-state projection, immutable bounded proof, and immutable replay result. Every artifact is schema 1 and
/// bound to a physical-workspace anchor. Roles remain declarations and never grant authority, approval, or credentials.
/// </remarks>
public sealed class ContextualRoleRevisionStore : IContextualRoleRevisionMutationPort, IContextualRoleRevisionReader, IContextualRoleLifecycleReader, IContextualRoleCatalogReader, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _workspaceId;
    private readonly ContextualRoleStorePaths _paths;
    private readonly ContextualRoleArtifactPathGuard _guard;
    private readonly ContextualRoleRevisionStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Initializes a contextual-role store rooted beneath the supplied workspace's <c>.agent</c> directory.</summary>
    /// <param name="workspacePaths">The canonical workspace paths.</param>
    /// <param name="workspaceId">The canonical workspace SHA-256 identity bound into every artifact.</param>
    /// <param name="options">Optional bounded persistence and recovery-evaluation settings.</param>
    /// <param name="timeProvider">The clock used for durable evidence timestamps.</param>
    /// <param name="authorityTransaction">The optional shared reentrant workspace authority fence.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="workspacePaths"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="workspaceId"/> is not a canonical workspace SHA-256 identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a configured persistence limit is outside the schema-1 safety ceilings.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the canonical workspace root does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the canonical workspace root is a symbolic link, reparse point, or junction.</exception>
    public ContextualRoleRevisionStore(
        WorkspacePaths workspacePaths,
        string workspaceId,
        ContextualRoleRevisionStoreOptions? options = null,
        TimeProvider? timeProvider = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(workspacePaths);
        if (!ContextualRoleWorkspaceId.IsValid(workspaceId))
        {
            throw new ArgumentException("Workspace id must be a canonical workspace SHA-256 identifier.", nameof(workspaceId));
        }

        _options = options ?? new ContextualRoleRevisionStoreOptions();
        ValidateOptions(_options);
        _workspaceId = workspaceId;
        _paths = new ContextualRoleStorePaths(workspacePaths);
        _guard = new ContextualRoleArtifactPathGuard(_paths, _options.PhysicalBoundaryObserver);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(workspacePaths);
    }

    /// <summary>Releases the retained physical-directory handles owned by this store.</summary>
    /// <remarks>Callers must not dispose the store while an operation is in flight. Disposal does not mutate persisted artifacts.</remarks>
    public void Dispose() => _guard.Dispose();

    /// <inheritdoc />
    public async Task<ContextualRoleRevisionMutationResult> MutateAsync(ContextualRoleRevisionMutationRequest request, CancellationToken cancellationToken = default)
    {
        var validationErrors = ContextualRoleRevisionMutationRequestValidator.Validate(request);
        if (validationErrors.Count != 0)
        {
            return new ContextualRoleRevisionMutationResult(ContextualRoleRevisionMutationStatus.Invalid, request?.OperationId ?? string.Empty, request?.RequestHash ?? string.Empty, request?.Kind ?? ContextualRoleRevisionMutationKind.Unknown, null, null, validationErrors.ToArray());
        }

        ContextualRoleRevisionMutationResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await MutateCoreAsync(request, token);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return callbackResult ?? Outcome(ContextualRoleRevisionMutationStatus.Unavailable, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
    }

    private async Task<ContextualRoleRevisionMutationResult> MutateCoreAsync(ContextualRoleRevisionMutationRequest request, CancellationToken cancellationToken)
    {
        var intentPublished = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _guard.PrepareRoots();
            using var mutationLock = _guard.TryAcquireMutationLock();
            if (mutationLock is null)
            {
                return Outcome(ContextualRoleRevisionMutationStatus.Unavailable, request, null, null);
            }

            _guard.CleanupTemporaryArtifacts();
            var anchor = await EnsureAnchorAsync(cancellationToken);
            await ValidateWorkspaceAsync(anchor, request.OperationId, cancellationToken);

            var result = await ReadResultIfExistsAsync(request.OperationId, anchor.IntegrityHash, cancellationToken);
            var intent = await ReadIntentIfExistsAsync(request.OperationId, anchor.IntegrityHash, cancellationToken);
            if (result is not null)
            {
                if (intent is null || !SameRequest(request, intent.Request))
                {
                    return Outcome(ContextualRoleRevisionMutationStatus.Conflict, request, null, null);
                }

                return ToPublic(result);
            }

            if (intent is not null)
            {
                if (!SameRequest(request, intent.Request))
                {
                    return Outcome(ContextualRoleRevisionMutationStatus.Conflict, request, null, null);
                }

                intentPublished = true;
                return await CompleteIntentAsync(intent, anchor, recovering: true, cancellationToken);
            }

            var current = await ReadStateIfExistsAsync(request.RoleId, anchor.IntegrityHash, cancellationToken);
            var planned = PlanIntent(request, current, anchor.IntegrityHash);
            await EnsureQuotaForIntentAsync(planned, anchor, cancellationToken);
            await _guard.WriteImmutableAsync(_paths.Intent(request.OperationId), ContextualRoleArtifactCodec.Serialize(planned), cancellationToken);
            intentPublished = true;
            await ObserveAsync(ContextualRolePersistenceBoundary.IntentPublished, cancellationToken);
            return await CompleteIntentAsync(planned, anchor, recovering: false, cancellationToken);
        }
        catch (OperationCanceledException exception) when (intentPublished)
        {
            return Outcome(ContextualRoleRevisionMutationStatus.Ambiguous, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
        catch (FormatException exception)
        {
            return Outcome(ContextualRoleRevisionMutationStatus.Ambiguous, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
        catch (ContextualRolePublicationAmbiguousException exception)
        {
            return Outcome(ContextualRoleRevisionMutationStatus.Ambiguous, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
        catch (ContextualRolePersistenceUnavailableException exception)
        {
            return Outcome(intentPublished ? ContextualRoleRevisionMutationStatus.Ambiguous : ContextualRoleRevisionMutationStatus.Unavailable, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
        catch (InvalidOperationException exception)
        {
            return Outcome(ContextualRoleRevisionMutationStatus.Ambiguous, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
        catch (IOException exception)
        {
            return Outcome(intentPublished ? ContextualRoleRevisionMutationStatus.Ambiguous : ContextualRoleRevisionMutationStatus.Unavailable, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Outcome(intentPublished ? ContextualRoleRevisionMutationStatus.Ambiguous : ContextualRoleRevisionMutationStatus.Unavailable, request, null, null, ContextualRoleArtifactPathGuard.GetMutationDiagnostic(exception));
        }
    }

    /// <inheritdoc />
    public async Task<ContextualRoleRevisionReadResult> ReadAsync(ContextualRoleRevisionReadRequest request, CancellationToken cancellationToken = default)
    {
        if (request?.Identity is not { } identity || !ContextualRoleId.IsValid(identity.RoleId) || identity.Revision < 1)
        {
            return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Invalid, null, ContextualRoleRevisionDisposition.Unknown, [new ContextualRoleValidationError("invalid_revision_identity", "identity", "Revision identity must contain a valid role id and positive revision.")]);
        }

        ContextualRoleRevisionReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadCoreAsync(identity, token);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return callbackResult ?? new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Unavailable, null, ContextualRoleRevisionDisposition.Unknown, []);
        }
    }

    private async Task<ContextualRoleRevisionReadResult> ReadCoreAsync(ContextualRoleRevisionIdentity identity, CancellationToken cancellationToken)
    {
        try
        {
            if (!_guard.StoreExists())
            {
                return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.NotFound, null, ContextualRoleRevisionDisposition.Unknown, []);
            }

            using var mutationLock = _guard.TryAcquireExistingMutationLock();
            if (mutationLock is null)
            {
                return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Unavailable, null, ContextualRoleRevisionDisposition.Unknown, []);
            }

            var anchor = await ReadRequiredAnchorAsync(cancellationToken);
            await ValidateWorkspaceAsync(anchor, allowedPendingOperationId: null, cancellationToken);
            var artifact = await ReadRevisionIfExistsAsync(identity, anchor.IntegrityHash, cancellationToken);
            if (artifact is null)
            {
                return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.NotFound, null, ContextualRoleRevisionDisposition.Unknown, []);
            }

            var state = await ReadStateIfExistsAsync(identity.RoleId, anchor.IntegrityHash, cancellationToken) ?? throw new FormatException("A retained contextual-role revision has no attributable lifecycle state.");
            var disposition = state.CurrentIdentity != identity
                ? ContextualRoleRevisionDisposition.Replaced
                : state.State switch
                {
                    ContextualRoleLifecycleState.Active => ContextualRoleRevisionDisposition.Active,
                    ContextualRoleLifecycleState.Disabled => ContextualRoleRevisionDisposition.Disabled,
                    ContextualRoleLifecycleState.Tombstoned => ContextualRoleRevisionDisposition.Tombstoned,
                    _ => throw new FormatException("A contextual-role primary state has no supported exact-revision disposition.")
                };
            return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Found, artifact.Revision, disposition, []);
        }
        catch (FormatException)
        {
            return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Ambiguous, null, ContextualRoleRevisionDisposition.Unknown, []);
        }
        catch (InvalidOperationException)
        {
            return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Ambiguous, null, ContextualRoleRevisionDisposition.Unknown, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ContextualRoleRevisionReadResult(ContextualRoleRevisionReadStatus.Unavailable, null, ContextualRoleRevisionDisposition.Unknown, []);
        }
    }

    /// <inheritdoc />
    public async Task<ContextualRoleLifecycleReadResult> ReadLifecycleAsync(ContextualRoleLifecycleReadRequest request, CancellationToken cancellationToken = default)
    {
        var roleId = request?.RoleId;
        if (!ContextualRoleId.IsValid(roleId))
        {
            return new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Invalid, null);
        }

        ContextualRoleLifecycleReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadLifecycleCoreAsync(roleId!, token);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return callbackResult ?? new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Unavailable, null);
        }
    }

    private async Task<ContextualRoleLifecycleReadResult> ReadLifecycleCoreAsync(string roleId, CancellationToken cancellationToken)
    {
        try
        {
            if (!_guard.StoreExists())
            {
                return new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.NotFound, null);
            }

            using var mutationLock = _guard.TryAcquireExistingMutationLock();
            if (mutationLock is null)
            {
                return new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Unavailable, null);
            }

            var anchor = await ReadRequiredAnchorAsync(cancellationToken);
            await ValidateWorkspaceAsync(anchor, allowedPendingOperationId: null, cancellationToken);
            var state = await ReadStateIfExistsAsync(roleId, anchor.IntegrityHash, cancellationToken);
            return state is null
                ? new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.NotFound, null)
                : new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Found, new ContextualRoleLifecycleSnapshot(SchemaVersion, state.RoleId, state.CurrentIdentity, state.State, state.LastOperationId, state.LastMutationKind, state.UpdatedAtUtc));
        }
        catch (FormatException)
        {
            return new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Ambiguous, null);
        }
        catch (InvalidOperationException)
        {
            return new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Ambiguous, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ContextualRoleLifecycleReadResult(ContextualRoleLifecycleReadStatus.Unavailable, null);
        }
    }

    /// <inheritdoc />
    public async Task<ContextualRoleCatalogReadResult> ReadCatalogAsync(ContextualRoleCatalogReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request is null
            || request.MaximumCount is < 1 or > ContextualRoleCatalogLimits.MaximumPageSize
            || request.StartAfterRoleId is not null && !ContextualRoleId.IsValid(request.StartAfterRoleId))
        {
            return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Invalid, [], null);
        }

        ContextualRoleCatalogReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadCatalogCoreAsync(request, token);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return callbackResult ?? new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Unavailable, [], null);
        }
    }

    private async Task<ContextualRoleCatalogReadResult> ReadCatalogCoreAsync(ContextualRoleCatalogReadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            if (!_guard.StoreExists())
            {
                return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, [], null);
            }

            using var mutationLock = _guard.TryAcquireExistingMutationLock();
            if (mutationLock is null)
            {
                return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Unavailable, [], null);
            }

            var anchor = await ReadRequiredAnchorAsync(cancellationToken);
            await ValidateWorkspaceAsync(anchor, allowedPendingOperationId: null, cancellationToken);
            var roleIds = _guard.EnumerateJsonFiles(_paths.States)
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .Where(roleId => request.StartAfterRoleId is null || string.Compare(roleId, request.StartAfterRoleId, StringComparison.Ordinal) > 0)
                .Order(StringComparer.Ordinal)
                .Take(request.MaximumCount + 1)
                .ToArray();
            var hasMore = roleIds.Length > request.MaximumCount;
            var pageRoleIds = hasMore ? roleIds[..request.MaximumCount] : roleIds;
            var entries = new List<ContextualRoleCatalogEntry>(pageRoleIds.Length);
            foreach (var roleId in pageRoleIds)
            {
                var state = await ReadStateIfExistsAsync(roleId, anchor.IntegrityHash, cancellationToken) ?? throw new FormatException("A cataloged contextual-role state disappeared after validation.");
                var revision = await ReadRevisionIfExistsAsync(state.CurrentIdentity, anchor.IntegrityHash, cancellationToken) ?? throw new FormatException("A cataloged contextual-role revision disappeared after validation.");
                entries.Add(new ContextualRoleCatalogEntry(
                    revision.Revision,
                    new ContextualRoleLifecycleSnapshot(SchemaVersion, state.RoleId, state.CurrentIdentity, state.State, state.LastOperationId, state.LastMutationKind, state.UpdatedAtUtc)));
            }

            return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Available, entries, hasMore ? entries[^1].Revision.Identity.RoleId : null);
        }
        catch (FormatException)
        {
            return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Ambiguous, [], null);
        }
        catch (InvalidOperationException)
        {
            return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Ambiguous, [], null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ContextualRoleCatalogReadResult(ContextualRoleCatalogReadStatus.Unavailable, [], null);
        }
    }

    private async Task<ContextualRoleRevisionMutationResult> CompleteIntentAsync(ContextualRoleMutationIntentArtifact intent, ContextualRoleWorkspaceAnchor anchor, bool recovering, CancellationToken cancellationToken)
    {
        var existingResult = await ReadResultIfExistsAsync(intent.Request.OperationId, anchor.IntegrityHash, cancellationToken);
        if (existingResult is not null)
        {
            return ToPublic(existingResult);
        }

        var current = await ReadStateIfExistsAsync(intent.Request.RoleId, anchor.IntegrityHash, cancellationToken);
        ContextualRoleRevisionMutationStatus terminalStatus;
        ContextualRolePrimaryStateArtifact? terminalState;
        if (intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Conflict)
        {
            if (!ContextualRoleArtifactCodec.Equivalent(current, intent.PriorState))
            {
                throw new FormatException("A pending contextual-role conflict intent no longer matches its proved predecessor.");
            }

            terminalStatus = ContextualRoleRevisionMutationStatus.Conflict;
            terminalState = current;
        }
        else
        {
            if (!ContextualRoleArtifactCodec.Equivalent(current, intent.PriorState) && !ContextualRoleArtifactCodec.Equivalent(current, intent.PlannedState))
            {
                throw new FormatException("A pending contextual-role mutation does not match either its predecessor or planned primary state.");
            }

            if (intent.Request.Kind is ContextualRoleRevisionMutationKind.Create or ContextualRoleRevisionMutationKind.Replace)
            {
                await EnsureRevisionPublishedAsync(intent.Request.Revision!, anchor, cancellationToken);
            }

            if (!ContextualRoleArtifactCodec.Equivalent(current, intent.PlannedState))
            {
                await PublishPrimaryAsync(intent.PriorState, intent.PlannedState!, cancellationToken);
                await ObserveAsync(ContextualRolePersistenceBoundary.PrimaryPublished, cancellationToken);
            }

            terminalStatus = recovering ? ContextualRoleRevisionMutationStatus.Recovered : ContextualRoleRevisionMutationStatus.Accepted;
            terminalState = intent.PlannedState;
        }

        var proof = await ReadProofIfExistsAsync(intent.Request.OperationId, anchor.IntegrityHash, cancellationToken);
        ContextualRoleLifecycleEvidence evidence;
        if (proof is null)
        {
            evidence = new ContextualRoleLifecycleEvidence(
                SchemaVersion,
                intent.Request.OperationId,
                intent.Request.RequestHash,
                intent.Request.Kind,
                intent.Request.RoleId,
                intent.Request.ActorId,
                intent.PriorState?.CurrentIdentity,
                intent.PriorState?.IntegrityHash,
                terminalState?.CurrentIdentity,
                terminalState?.IntegrityHash,
                terminalState?.Sequence ?? 0,
                terminalState?.State ?? ContextualRoleLifecycleState.Absent,
                terminalStatus,
                intent.Request.RequestedAtUtc,
                _timeProvider.GetUtcNow(),
                recovering);
            proof = ContextualRoleArtifactCodec.Seal(new ContextualRoleLifecycleProofArtifact(SchemaVersion, anchor.IntegrityHash, evidence, string.Empty));
            EnsureCapacity(ContextualRoleArtifactCodec.Serialize(proof).Length, replacingPath: null);
            await _guard.WriteImmutableAsync(_paths.Proof(intent.Request.OperationId), ContextualRoleArtifactCodec.Serialize(proof), cancellationToken);
            await ObserveAsync(ContextualRolePersistenceBoundary.ProofPublished, cancellationToken);
        }
        else
        {
            evidence = proof.Evidence;
            terminalStatus = evidence.Outcome;
        }

        var revision = terminalState is null ? null : (await ReadRevisionIfExistsAsync(terminalState.CurrentIdentity, anchor.IntegrityHash, cancellationToken))?.Revision;
        if (terminalState is not null && revision is null)
        {
            throw new FormatException("A contextual-role terminal outcome does not retain its exact current immutable revision.");
        }

        var result = ContextualRoleArtifactCodec.Seal(new ContextualRoleMutationResultArtifact(SchemaVersion, anchor.IntegrityHash, terminalStatus, intent.Request.OperationId, intent.Request.RequestHash, intent.Request.Kind, revision, evidence, string.Empty));
        EnsureCapacity(ContextualRoleArtifactCodec.Serialize(result).Length, replacingPath: null);
        await _guard.WriteImmutableAsync(_paths.Result(intent.Request.OperationId), ContextualRoleArtifactCodec.Serialize(result), cancellationToken);
        await ObserveAsync(ContextualRolePersistenceBoundary.ResultPublished, cancellationToken);
        return ToPublic(result);
    }

    private ContextualRoleMutationIntentArtifact PlanIntent(ContextualRoleRevisionMutationRequest request, ContextualRolePrimaryStateArtifact? current, string anchorHash)
    {
        var accepted = IsAcceptedTransition(request, current);
        ContextualRolePrimaryStateArtifact? planned = current;
        if (accepted)
        {
            var identity = request.Revision?.Identity ?? current!.CurrentIdentity;
            var state = request.Kind switch
            {
                ContextualRoleRevisionMutationKind.Disable => ContextualRoleLifecycleState.Disabled,
                ContextualRoleRevisionMutationKind.Replace => current?.State ?? ContextualRoleLifecycleState.Active,
                ContextualRoleRevisionMutationKind.Tombstone => ContextualRoleLifecycleState.Tombstoned,
                _ => ContextualRoleLifecycleState.Active
            };
            planned = ContextualRoleArtifactCodec.Seal(new ContextualRolePrimaryStateArtifact(SchemaVersion, anchorHash, request.RoleId, identity, state, request.OperationId, request.Kind, checked((current?.Sequence ?? 0) + 1), _timeProvider.GetUtcNow(), string.Empty));
        }

        var intendedOutcome = accepted ? ContextualRoleRevisionMutationStatus.Accepted : ContextualRoleRevisionMutationStatus.Conflict;
        return ContextualRoleArtifactCodec.Seal(new ContextualRoleMutationIntentArtifact(SchemaVersion, anchorHash, request, current, planned, intendedOutcome, _timeProvider.GetUtcNow(), string.Empty));
    }

    private static bool IsAcceptedTransition(ContextualRoleRevisionMutationRequest request, ContextualRolePrimaryStateArtifact? current)
    {
        if (request.Kind == ContextualRoleRevisionMutationKind.Create)
        {
            return current is null;
        }

        if (current is null || current.State == ContextualRoleLifecycleState.Tombstoned || request.ExpectedPreviousIdentity != current.CurrentIdentity)
        {
            return false;
        }

        return request.Kind switch
        {
            ContextualRoleRevisionMutationKind.Replace => true,
            ContextualRoleRevisionMutationKind.Disable => current.State == ContextualRoleLifecycleState.Active,
            ContextualRoleRevisionMutationKind.Reenable => current.State == ContextualRoleLifecycleState.Disabled,
            ContextualRoleRevisionMutationKind.Tombstone => current.State is ContextualRoleLifecycleState.Active or ContextualRoleLifecycleState.Disabled,
            _ => false
        };
    }

    private async Task PublishPrimaryAsync(ContextualRolePrimaryStateArtifact? prior, ContextualRolePrimaryStateArtifact planned, CancellationToken cancellationToken)
    {
        var bytes = ContextualRoleArtifactCodec.Serialize(planned);
        var path = _paths.State(planned.RoleId);
        EnsureCapacity(bytes.Length, prior is null ? null : path);
        if (prior is null)
        {
            await _guard.WriteImmutableAsync(path, bytes, cancellationToken);
        }
        else
        {
            await _guard.WriteProjectionAsync(path, bytes, cancellationToken);
        }
    }

    private async Task EnsureRevisionPublishedAsync(ContextualRoleRevision revision, ContextualRoleWorkspaceAnchor anchor, CancellationToken cancellationToken)
    {
        var path = _paths.Revision(revision.Identity.RoleId, revision.Identity.Revision);
        var planned = ContextualRoleArtifactCodec.Seal(new ContextualRoleRevisionArtifact(SchemaVersion, anchor.IntegrityHash, revision, string.Empty));
        var existing = await ReadRevisionIfExistsAsync(revision.Identity, anchor.IntegrityHash, cancellationToken);
        if (existing is not null)
        {
            if (!string.Equals(existing.IntegrityHash, planned.IntegrityHash, StringComparison.Ordinal))
            {
                throw new FormatException("An immutable contextual-role revision identity was reused with changed content.");
            }

            return;
        }

        var bytes = ContextualRoleArtifactCodec.Serialize(planned);
        EnsureCapacity(bytes.Length, replacingPath: null);
        await _guard.WriteImmutableAsync(path, bytes, cancellationToken);
        await ObserveAsync(ContextualRolePersistenceBoundary.RevisionPublished, cancellationToken);
    }

    private async Task<ContextualRoleWorkspaceAnchor> EnsureAnchorAsync(CancellationToken cancellationToken)
    {
        if (_guard.FileExists(_paths.Anchor))
        {
            return await ReadRequiredAnchorAsync(cancellationToken);
        }

        var hasArtifacts = _guard.EnumerateJsonFiles(_paths.Revisions).Count != 0
            || _guard.EnumerateJsonFiles(_paths.States).Count != 0
            || _guard.EnumerateJsonFiles(_paths.Operations).Count != 0
            || _guard.EnumerateJsonFiles(_paths.Proofs).Count != 0;
        if (hasArtifacts)
        {
            throw new FormatException("Contextual-role artifacts exist without their required physical-workspace anchor.");
        }

        var anchor = ContextualRoleArtifactCodec.Seal(new ContextualRoleWorkspaceAnchor(SchemaVersion, _workspaceId, _guard.CanonicalRootHash, _guard.RootCreationTimeUtcTicks, _timeProvider.GetUtcNow(), string.Empty));
        await _guard.WriteImmutableAsync(_paths.Anchor, ContextualRoleArtifactCodec.Serialize(anchor), cancellationToken);
        await ObserveAsync(ContextualRolePersistenceBoundary.AnchorPublished, cancellationToken);
        return anchor;
    }

    private async Task<ContextualRoleWorkspaceAnchor> ReadRequiredAnchorAsync(CancellationToken cancellationToken)
    {
        if (!_guard.FileExists(_paths.Anchor))
        {
            throw new FormatException("Contextual-role persistence is missing its required physical-workspace anchor.");
        }

        var anchor = ContextualRoleArtifactCodec.Deserialize<ContextualRoleWorkspaceAnchor>(await _guard.ReadAsync(_paths.Anchor, cancellationToken), "Contextual-role workspace anchor");
        ContextualRoleArtifactCodec.Validate(anchor);
        if (!string.Equals(anchor.WorkspaceId, _workspaceId, StringComparison.Ordinal)
            || !string.Equals(anchor.CanonicalRootHash, _guard.CanonicalRootHash, StringComparison.Ordinal)
            || anchor.RootCreationTimeUtcTicks != _guard.RootCreationTimeUtcTicks)
        {
            throw new FormatException("Contextual-role artifacts belong to a different physical workspace.");
        }

        return anchor;
    }

    private async Task ValidateWorkspaceAsync(ContextualRoleWorkspaceAnchor anchor, string? allowedPendingOperationId, CancellationToken cancellationToken)
    {
        _guard.VerifyWorkspaceIdentity();
        _guard.ValidateKnownLayout();
        var revisionFiles = _guard.EnumerateJsonFiles(_paths.Revisions);
        var stateFiles = _guard.EnumerateJsonFiles(_paths.States);
        var operationFiles = _guard.EnumerateJsonFiles(_paths.Operations);
        var proofFiles = _guard.EnumerateJsonFiles(_paths.Proofs);
        if (revisionFiles.Count > _options.MaxRevisionArtifacts || operationFiles.Count(path => path.EndsWith(".intent.json", StringComparison.Ordinal)) > _options.MaxOperationArtifacts || _guard.CountArtifactBytes() > _options.MaxTotalArtifactBytes)
        {
            throw new FormatException("Contextual-role persistence exceeds its bounded workspace quota.");
        }

        var revisions = new Dictionary<ContextualRoleRevisionIdentity, ContextualRoleRevisionArtifact>();
        foreach (var path in revisionFiles)
        {
            var artifact = ContextualRoleArtifactCodec.Deserialize<ContextualRoleRevisionArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role revision");
            ContextualRoleArtifactCodec.Validate(artifact, anchor.IntegrityHash);
            if (!string.Equals(Path.GetFileName(path), $"{artifact.Revision.Identity.RoleId}.{artifact.Revision.Identity.Revision}.json", StringComparison.Ordinal) || !revisions.TryAdd(artifact.Revision.Identity, artifact))
            {
                throw new FormatException("Contextual-role persistence contains a duplicated or misnamed immutable revision.");
            }
        }

        var states = new Dictionary<string, ContextualRolePrimaryStateArtifact>(StringComparer.Ordinal);
        foreach (var path in stateFiles)
        {
            var state = ContextualRoleArtifactCodec.Deserialize<ContextualRolePrimaryStateArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role primary state");
            ContextualRoleArtifactCodec.Validate(state, anchor.IntegrityHash);
            if (!string.Equals(Path.GetFileName(path), $"{state.RoleId}.json", StringComparison.Ordinal) || !states.TryAdd(state.RoleId, state) || !revisions.ContainsKey(state.CurrentIdentity))
            {
                throw new FormatException("Contextual-role persistence contains a misnamed state or a state without its exact immutable revision.");
            }
        }

        var intents = new Dictionary<string, ContextualRoleMutationIntentArtifact>(StringComparer.Ordinal);
        var results = new Dictionary<string, ContextualRoleMutationResultArtifact>(StringComparer.Ordinal);
        foreach (var path in operationFiles)
        {
            if (path.EndsWith(".intent.json", StringComparison.Ordinal))
            {
                var intent = ContextualRoleArtifactCodec.Deserialize<ContextualRoleMutationIntentArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role mutation intent");
                ContextualRoleArtifactCodec.Validate(intent, anchor.IntegrityHash);
                if (!string.Equals(Path.GetFileName(path), $"{intent.Request.OperationId}.intent.json", StringComparison.Ordinal) || !intents.TryAdd(intent.Request.OperationId, intent))
                {
                    throw new FormatException("Contextual-role persistence contains a duplicated or misnamed mutation intent.");
                }
            }
            else if (path.EndsWith(".result.json", StringComparison.Ordinal))
            {
                var result = ContextualRoleArtifactCodec.Deserialize<ContextualRoleMutationResultArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role mutation result");
                ContextualRoleArtifactCodec.Validate(result, anchor.IntegrityHash);
                if (!string.Equals(Path.GetFileName(path), $"{result.OperationId}.result.json", StringComparison.Ordinal) || !results.TryAdd(result.OperationId, result))
                {
                    throw new FormatException("Contextual-role persistence contains a duplicated or misnamed mutation result.");
                }
            }
            else
            {
                throw new FormatException("Contextual-role persistence contains an unknown operation artifact.");
            }
        }

        var proofs = new Dictionary<string, ContextualRoleLifecycleProofArtifact>(StringComparer.Ordinal);
        foreach (var path in proofFiles)
        {
            var proof = ContextualRoleArtifactCodec.Deserialize<ContextualRoleLifecycleProofArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role lifecycle proof");
            ContextualRoleArtifactCodec.Validate(proof, anchor.IntegrityHash);
            if (!string.Equals(Path.GetFileName(path), $"{proof.Evidence.OperationId}.json", StringComparison.Ordinal) || !proofs.TryAdd(proof.Evidence.OperationId, proof))
            {
                throw new FormatException("Contextual-role persistence contains a duplicated or misnamed lifecycle proof.");
            }
        }

        var referencedRevisions = new HashSet<ContextualRoleRevisionIdentity>(states.Values.Select(state => state.CurrentIdentity));
        foreach (var (operationId, intent) in intents)
        {
            var hasResult = results.TryGetValue(operationId, out var result);
            var hasProof = proofs.TryGetValue(operationId, out var proof);
            if (!hasResult && !string.Equals(operationId, allowedPendingOperationId, StringComparison.Ordinal)
                || hasResult && !hasProof
                || hasProof && (!SameRequest(intent.Request, proof!.Evidence) || !ProofMatchesIntent(intent, proof) || hasResult && !SameOutcome(result!, proof)))
            {
                throw new FormatException("Contextual-role operation history contains incomplete or mismatched immutable evidence.");
            }

            if (hasResult && result!.Revision is { } resultRevision)
            {
                if (result.Evidence.CurrentIdentity != resultRevision.Identity
                    || !revisions.TryGetValue(resultRevision.Identity, out var retained)
                    || !string.Equals(retained.IntegrityHash, ContextualRoleArtifactCodec.Seal(new ContextualRoleRevisionArtifact(SchemaVersion, anchor.IntegrityHash, resultRevision, string.Empty)).IntegrityHash, StringComparison.Ordinal))
                {
                    throw new FormatException("Contextual-role replay result does not match its retained immutable revision.");
                }
            }
            else if (hasResult && result!.Evidence.CurrentIdentity is not null)
            {
                throw new FormatException("Contextual-role replay result omitted its proved current immutable revision.");
            }

            if (proof?.Evidence.PreviousIdentity is { } previous)
            {
                referencedRevisions.Add(previous);
            }

            if (proof?.Evidence.CurrentIdentity is { } current)
            {
                referencedRevisions.Add(current);
            }

            if (!hasResult && intent.Request.Revision?.Identity is { } pendingRevision)
            {
                referencedRevisions.Add(pendingRevision);
            }
        }

        ValidateTransitionChains(states, intents, results, proofs, allowedPendingOperationId);

        if (results.Keys.Except(intents.Keys, StringComparer.Ordinal).Any() || proofs.Keys.Except(intents.Keys, StringComparer.Ordinal).Any() || revisions.Keys.Except(referencedRevisions).Any())
        {
            throw new FormatException("Contextual-role persistence contains orphaned result, proof, or revision artifacts.");
        }

        foreach (var state in states.Values)
        {
            if (!intents.ContainsKey(state.LastOperationId) || !results.ContainsKey(state.LastOperationId) && !string.Equals(state.LastOperationId, allowedPendingOperationId, StringComparison.Ordinal))
            {
                throw new FormatException("Contextual-role primary state is not attributable to a proved or explicitly recoverable operation.");
            }
        }

        _guard.VerifyWorkspaceIdentity();
    }

    private static void ValidateTransitionChains(
        IReadOnlyDictionary<string, ContextualRolePrimaryStateArtifact> states,
        IReadOnlyDictionary<string, ContextualRoleMutationIntentArtifact> intents,
        IReadOnlyDictionary<string, ContextualRoleMutationResultArtifact> results,
        IReadOnlyDictionary<string, ContextualRoleLifecycleProofArtifact> proofs,
        string? allowedPendingOperationId)
    {
        foreach (var conflict in intents.Values.Where(intent => intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Conflict))
        {
            if (!ContextualRoleArtifactCodec.Equivalent(conflict.PriorState, conflict.PlannedState) || IsAcceptedTransition(conflict.Request, conflict.PriorState))
            {
                throw new FormatException("A contextual-role conflict intent attempted a transition or contradicted its captured predecessor.");
            }
        }

        var acceptedGroups = intents.Values
            .Where(intent => intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted)
            .GroupBy(intent => intent.Request.RoleId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(intent => intent.PlannedState!.Sequence).ToArray(), StringComparer.Ordinal);
        foreach (var (roleId, ordered) in acceptedGroups)
        {
            ContextualRolePrimaryStateArtifact? predecessor = null;
            for (var index = 0; index < ordered.Length; index++)
            {
                var intent = ordered[index];
                var planned = intent.PlannedState ?? throw new FormatException("An accepted contextual-role transition omitted its immutable planned state.");
                if (!ContextualRoleArtifactCodec.Equivalent(intent.PriorState, predecessor)
                    || planned.Sequence != checked((predecessor?.Sequence ?? 0) + 1)
                    || !string.Equals(planned.RoleId, roleId, StringComparison.Ordinal)
                    || !string.Equals(planned.LastOperationId, intent.Request.OperationId, StringComparison.Ordinal)
                    || planned.LastMutationKind != intent.Request.Kind
                    || !IsAcceptedTransition(intent.Request, predecessor)
                    || !TransitionMatchesRequest(intent, predecessor, planned))
                {
                    throw new FormatException("Contextual-role transition history contains a fork, gap, reorder, or semantically invalid state change.");
                }

                var completed = results.ContainsKey(intent.Request.OperationId);
                if (!completed)
                {
                    if (!string.Equals(intent.Request.OperationId, allowedPendingOperationId, StringComparison.Ordinal) || index != ordered.Length - 1)
                    {
                        throw new FormatException("Only the current tail intent may remain incomplete during contextual-role recovery.");
                    }

                    var recoverablePrimary = states.GetValueOrDefault(roleId);
                    var proofPublished = proofs.ContainsKey(intent.Request.OperationId);
                    if (proofPublished
                        ? !ContextualRoleArtifactCodec.Equivalent(recoverablePrimary, planned)
                        : !ContextualRoleArtifactCodec.Equivalent(recoverablePrimary, predecessor) && !ContextualRoleArtifactCodec.Equivalent(recoverablePrimary, planned))
                    {
                        throw new FormatException("A recoverable contextual-role tail is not at its exact predecessor or planned primary state.");
                    }
                }

                predecessor = planned;
            }

            var pendingTail = ordered[^1];
            if (results.ContainsKey(pendingTail.Request.OperationId)
                && (!states.TryGetValue(roleId, out var current) || !ContextualRoleArtifactCodec.Equivalent(current, predecessor)))
            {
                throw new FormatException("The contextual-role primary projection is not the exact terminal tail of its immutable transition chain.");
            }
        }

        foreach (var conflict in intents.Values.Where(intent => intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Conflict && intent.PriorState is not null))
        {
            if (!acceptedGroups.TryGetValue(conflict.Request.RoleId, out var roleTransitions)
                || !roleTransitions.Any(accepted => ContextualRoleArtifactCodec.Equivalent(accepted.PlannedState, conflict.PriorState)))
            {
                throw new FormatException("A contextual-role conflict references a predecessor outside the role's immutable transition chain.");
            }
        }

        if (states.Keys.Except(acceptedGroups.Keys, StringComparer.Ordinal).Any())
        {
            throw new FormatException("A contextual-role primary projection exists without an accepted immutable transition chain.");
        }
    }

    private static bool TransitionMatchesRequest(ContextualRoleMutationIntentArtifact intent, ContextualRolePrimaryStateArtifact? predecessor, ContextualRolePrimaryStateArtifact planned)
    {
        var request = intent.Request;
        if (request.Kind == ContextualRoleRevisionMutationKind.Create)
        {
            return predecessor is null
                && request.ExpectedPreviousIdentity is null
                && request.Revision?.Identity == planned.CurrentIdentity
                && planned.State == ContextualRoleLifecycleState.Active;
        }

        if (predecessor is null || request.ExpectedPreviousIdentity != predecessor.CurrentIdentity)
        {
            return false;
        }

        return request.Kind switch
        {
            ContextualRoleRevisionMutationKind.Replace => request.Revision?.Identity == planned.CurrentIdentity && planned.State == predecessor.State,
            ContextualRoleRevisionMutationKind.Disable => planned.CurrentIdentity == predecessor.CurrentIdentity && predecessor.State == ContextualRoleLifecycleState.Active && planned.State == ContextualRoleLifecycleState.Disabled,
            ContextualRoleRevisionMutationKind.Reenable => planned.CurrentIdentity == predecessor.CurrentIdentity && predecessor.State == ContextualRoleLifecycleState.Disabled && planned.State == ContextualRoleLifecycleState.Active,
            ContextualRoleRevisionMutationKind.Tombstone => planned.CurrentIdentity == predecessor.CurrentIdentity && predecessor.State is ContextualRoleLifecycleState.Active or ContextualRoleLifecycleState.Disabled && planned.State == ContextualRoleLifecycleState.Tombstoned,
            _ => false
        };
    }

    private async Task<ContextualRoleRevisionArtifact?> ReadRevisionIfExistsAsync(ContextualRoleRevisionIdentity identity, string anchorHash, CancellationToken cancellationToken)
    {
        var path = _paths.Revision(identity.RoleId, identity.Revision);
        if (!_guard.FileExists(path))
        {
            return null;
        }

        var artifact = ContextualRoleArtifactCodec.Deserialize<ContextualRoleRevisionArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role revision");
        ContextualRoleArtifactCodec.Validate(artifact, anchorHash);
        if (artifact.Revision.Identity != identity)
        {
            throw new FormatException("Contextual-role revision filename identity does not match its immutable content.");
        }

        return artifact;
    }

    private async Task<ContextualRolePrimaryStateArtifact?> ReadStateIfExistsAsync(string roleId, string anchorHash, CancellationToken cancellationToken)
    {
        var path = _paths.State(roleId);
        if (!_guard.FileExists(path))
        {
            return null;
        }

        var state = ContextualRoleArtifactCodec.Deserialize<ContextualRolePrimaryStateArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role primary state");
        ContextualRoleArtifactCodec.Validate(state, anchorHash);
        if (!string.Equals(state.RoleId, roleId, StringComparison.Ordinal))
        {
            throw new FormatException("Contextual-role primary-state filename does not match its stable role identity.");
        }

        return state;
    }

    private async Task<ContextualRoleMutationIntentArtifact?> ReadIntentIfExistsAsync(string operationId, string anchorHash, CancellationToken cancellationToken)
    {
        var path = _paths.Intent(operationId);
        if (!_guard.FileExists(path))
        {
            return null;
        }

        var intent = ContextualRoleArtifactCodec.Deserialize<ContextualRoleMutationIntentArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role mutation intent");
        ContextualRoleArtifactCodec.Validate(intent, anchorHash);
        return intent;
    }

    private async Task<ContextualRoleLifecycleProofArtifact?> ReadProofIfExistsAsync(string operationId, string anchorHash, CancellationToken cancellationToken)
    {
        var path = _paths.Proof(operationId);
        if (!_guard.FileExists(path))
        {
            return null;
        }

        var proof = ContextualRoleArtifactCodec.Deserialize<ContextualRoleLifecycleProofArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role lifecycle proof");
        ContextualRoleArtifactCodec.Validate(proof, anchorHash);
        return proof;
    }

    private async Task<ContextualRoleMutationResultArtifact?> ReadResultIfExistsAsync(string operationId, string anchorHash, CancellationToken cancellationToken)
    {
        var path = _paths.Result(operationId);
        if (!_guard.FileExists(path))
        {
            return null;
        }

        var result = ContextualRoleArtifactCodec.Deserialize<ContextualRoleMutationResultArtifact>(await _guard.ReadAsync(path, cancellationToken), "Contextual-role mutation result");
        ContextualRoleArtifactCodec.Validate(result, anchorHash);
        return result;
    }

    private async Task EnsureQuotaForIntentAsync(ContextualRoleMutationIntentArtifact intent, ContextualRoleWorkspaceAnchor anchor, CancellationToken cancellationToken)
    {
        var revisionCount = _guard.EnumerateJsonFiles(_paths.Revisions).Count;
        var operationCount = _guard.EnumerateJsonFiles(_paths.Operations).Count(path => path.EndsWith(".intent.json", StringComparison.Ordinal));
        var publishesRevision = intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted && intent.Request.Kind is ContextualRoleRevisionMutationKind.Create or ContextualRoleRevisionMutationKind.Replace;
        if (operationCount >= _options.MaxOperationArtifacts || publishesRevision && revisionCount >= _options.MaxRevisionArtifacts)
        {
            throw new ContextualRolePersistenceUnavailableException("Contextual-role persistence quota is exhausted; no operation intent was published.");
        }

        var terminalState = intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted ? intent.PlannedState : intent.PriorState;
        var status = intent.IntendedOutcome;
        var evidence = new ContextualRoleLifecycleEvidence(SchemaVersion, intent.Request.OperationId, intent.Request.RequestHash, intent.Request.Kind, intent.Request.RoleId, intent.Request.ActorId, intent.PriorState?.CurrentIdentity, intent.PriorState?.IntegrityHash, terminalState?.CurrentIdentity, terminalState?.IntegrityHash, terminalState?.Sequence ?? 0, terminalState?.State ?? ContextualRoleLifecycleState.Absent, status, intent.Request.RequestedAtUtc, _timeProvider.GetUtcNow(), false);
        var proof = ContextualRoleArtifactCodec.Seal(new ContextualRoleLifecycleProofArtifact(SchemaVersion, anchor.IntegrityHash, evidence, string.Empty));
        var terminalRevision = publishesRevision
            ? intent.Request.Revision
            : terminalState is null
                ? null
                : (await ReadRevisionIfExistsAsync(terminalState.CurrentIdentity, anchor.IntegrityHash, cancellationToken))?.Revision ?? throw new FormatException("A contextual-role terminal outcome does not retain its exact current immutable revision.");
        var result = ContextualRoleArtifactCodec.Seal(new ContextualRoleMutationResultArtifact(SchemaVersion, anchor.IntegrityHash, status, intent.Request.OperationId, intent.Request.RequestHash, intent.Request.Kind, terminalRevision, evidence, string.Empty));
        var estimatedBytes = ContextualRoleArtifactCodec.Serialize(intent).Length + ContextualRoleArtifactCodec.Serialize(proof).Length + ContextualRoleArtifactCodec.Serialize(result).Length;
        if (publishesRevision)
        {
            estimatedBytes += ContextualRoleArtifactCodec.Serialize(ContextualRoleArtifactCodec.Seal(new ContextualRoleRevisionArtifact(SchemaVersion, anchor.IntegrityHash, intent.Request.Revision!, string.Empty))).Length;
        }

        if (intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted && intent.PlannedState is not null)
        {
            estimatedBytes += ContextualRoleArtifactCodec.Serialize(intent.PlannedState).Length;
        }

        var replacesPrimaryState = intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted && intent.PlannedState is not null;
        var replacedBytes = !replacesPrimaryState || intent.PriorState is null || !_guard.FileExists(_paths.State(intent.Request.RoleId)) ? 0 : _guard.GetFileLength(_paths.State(intent.Request.RoleId));
        if (_guard.CountArtifactBytes() + estimatedBytes - replacedBytes > _options.MaxTotalArtifactBytes)
        {
            throw new ContextualRolePersistenceUnavailableException("Contextual-role persistence byte quota is exhausted; no operation intent was published.");
        }
    }

    private void EnsureCapacity(int newBytes, string? replacingPath)
    {
        var replacedBytes = replacingPath is not null && _guard.FileExists(replacingPath) ? _guard.GetFileLength(replacingPath) : 0;
        if (_guard.CountArtifactBytes() + newBytes - replacedBytes > _options.MaxTotalArtifactBytes)
        {
            throw new ContextualRolePersistenceUnavailableException("Contextual-role persistence byte quota was exhausted during a durable operation.");
        }
    }

    private async ValueTask ObserveAsync(ContextualRolePersistenceBoundary boundary, CancellationToken cancellationToken)
    {
        if (_options.DurableBoundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken);
        }
    }

    private static bool SameRequest(ContextualRoleRevisionMutationRequest left, ContextualRoleRevisionMutationRequest right)
        => string.Equals(left.OperationId, right.OperationId, StringComparison.Ordinal) && string.Equals(left.RequestHash, right.RequestHash, StringComparison.Ordinal);

    private static bool SameRequest(ContextualRoleRevisionMutationRequest request, ContextualRoleLifecycleEvidence evidence)
        => string.Equals(request.OperationId, evidence.OperationId, StringComparison.Ordinal)
            && string.Equals(request.RequestHash, evidence.RequestHash, StringComparison.Ordinal)
            && request.Kind == evidence.Kind
            && string.Equals(request.RoleId, evidence.RoleId, StringComparison.Ordinal)
            && string.Equals(request.ActorId, evidence.ActorId, StringComparison.Ordinal);

    private static bool SameOutcome(ContextualRoleMutationResultArtifact result, ContextualRoleLifecycleProofArtifact proof)
        => result.Evidence == proof.Evidence && result.Status == proof.Evidence.Outcome;

    private static bool ProofMatchesIntent(ContextualRoleMutationIntentArtifact intent, ContextualRoleLifecycleProofArtifact proof)
    {
        var terminal = intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted ? intent.PlannedState : intent.PriorState;
        var validOutcome = intent.IntendedOutcome == ContextualRoleRevisionMutationStatus.Accepted
            ? proof.Evidence.Outcome is ContextualRoleRevisionMutationStatus.Accepted or ContextualRoleRevisionMutationStatus.Recovered
            : proof.Evidence.Outcome == ContextualRoleRevisionMutationStatus.Conflict;
        return validOutcome
            && proof.Evidence.PreviousIdentity == intent.PriorState?.CurrentIdentity
            && string.Equals(proof.Evidence.PreviousStateHash, intent.PriorState?.IntegrityHash, StringComparison.Ordinal)
            && proof.Evidence.CurrentIdentity == terminal?.CurrentIdentity
            && string.Equals(proof.Evidence.CurrentStateHash, terminal?.IntegrityHash, StringComparison.Ordinal)
            && proof.Evidence.Sequence == (terminal?.Sequence ?? 0)
            && proof.Evidence.State == (terminal?.State ?? ContextualRoleLifecycleState.Absent);
    }

    private static ContextualRoleRevisionMutationResult ToPublic(ContextualRoleMutationResultArtifact result)
        => new(result.Status, result.OperationId, result.RequestHash, result.Kind, result.Revision, result.Evidence, []);

    private static ContextualRoleRevisionMutationResult Outcome(ContextualRoleRevisionMutationStatus status, ContextualRoleRevisionMutationRequest request, ContextualRoleRevision? revision, ContextualRoleLifecycleEvidence? evidence, ContextualRoleRevisionMutationDiagnostic? diagnostic = null)
        => new ContextualRoleRevisionMutationResult(status, request.OperationId, request.RequestHash, request.Kind, revision, evidence, []) { Diagnostic = diagnostic };

    private static void ValidateOptions(ContextualRoleRevisionStoreOptions options)
    {
        if (options.MaxRevisionArtifacts is < 1 or > ContextualRoleRevisionStoreOptions.MaximumRevisionArtifacts
            || options.MaxOperationArtifacts is < 1 or > ContextualRoleRevisionStoreOptions.MaximumOperationArtifacts
            || options.MaxTotalArtifactBytes is < 4_096 or > ContextualRoleRevisionStoreOptions.MaximumTotalArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Contextual-role persistence limits must be positive and cannot exceed the schema-1 safety ceilings.");
        }
    }
}
