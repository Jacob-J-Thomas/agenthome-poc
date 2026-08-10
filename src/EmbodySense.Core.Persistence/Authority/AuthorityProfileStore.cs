using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Governance.Authority;
using EmbodySense.Core.Application.Governance.Authority.Grants;
using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
using EmbodySense.Core.Common.Authority.Grants;
using EmbodySense.Core.Common.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Authority.Models;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Authority;

/// <summary>Persists bounded authority-profile declarations and immutable lifecycle evidence without granting authority.</summary>
/// <remarks>
/// Workspace artifacts are untrusted until authenticated by a server-owned trust provider bound to the physical workspace
/// identity. Writes retain the last proved state before atomically replacing the primary and advancing the monotonic proof.
/// A proof mismatch, corrupt artifact, owner loss, or substitution is read-only recovered state or unavailable; neither path
/// is a mutation base. This store only preserves declarations and evidence: profile existence is never a role binding, grant,
/// delegation, admission decision, or runtime enforcement decision.
/// </remarks>
public sealed class AuthorityProfileStore : IAuthorityProfileStore, IAuthorityGrantStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(false);
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private readonly WorkspacePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly TimeProvider _timeProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates a store with the default server-owned proof provider.</summary>
    /// <param name="paths">The workspace paths that bound authority artifacts.</param>
    /// <param name="timeProvider">The optional trusted store clock.</param>
    /// <param name="durabilityBarrier">The optional post-rename durability boundary.</param>
    /// <param name="authorityTransaction">The optional shared reentrant workspace authority fence.</param>
    public AuthorityProfileStore(
        WorkspacePaths paths,
        TimeProvider? timeProvider = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
        : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), timeProvider, durabilityBarrier, authorityTransaction)
    {
    }

    /// <summary>Creates a store over an explicit server-owned proof provider.</summary>
    /// <param name="paths">The workspace paths that bound authority artifacts.</param>
    /// <param name="trustProvider">The server-owned provider that authenticates workspace state.</param>
    /// <param name="timeProvider">The optional trusted store clock.</param>
    /// <param name="durabilityBarrier">The optional post-rename durability boundary.</param>
    /// <param name="authorityTransaction">The optional shared reentrant workspace authority fence.</param>
    public AuthorityProfileStore(
        WorkspacePaths paths,
        ICapabilityCatalogTrustProvider trustProvider,
        TimeProvider? timeProvider = null,
        ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null,
        ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        if (trustProvider.MaximumAuthenticationTagUtf8Bytes < 1
            || trustProvider.MaximumAuthenticationTagUtf8Bytes > AuthorityProfileStoreLimits.MaximumArtifactUtf8Bytes / 6)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The authority trust provider must declare a positive bounded authentication-tag size.");
        }

        trustProvider.RequireDisjointWorkspace(paths.RootPath);
        _paths = paths;
        _pathGuard = new CapabilityCatalogPathGuard(paths.RootPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
        _trustProvider = trustProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public async Task<AuthorityProfileReadResult> ReadAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!AuthorityProfileId.TryParse(profileId, out var id, out _))
        {
            return new AuthorityProfileReadResult(AuthorityProfileReadStatus.Unavailable, null, "The profile query is outside the bounded schema-1 contract.");
        }

        try
        {
            return await _authorityTransaction.ExecuteAsync(token => ReadCoreAsync(id!, token), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return new AuthorityProfileReadResult(AuthorityProfileReadStatus.Unavailable, null, "The authority profile could not be read safely.");
        }
    }

    private async Task<AuthorityProfileReadResult> ReadCoreAsync(AuthorityProfileId profileId, CancellationToken cancellationToken)
    {
        await using var session = await AcquireLockAsync(cancellationToken);
        var identity = CreateWorkspaceIdentity(session.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
        var loaded = await LoadAsync(session, identity, trust, cancellationToken);
        if (loaded.Ambiguous)
        {
            return new AuthorityProfileReadResult(AuthorityProfileReadStatus.Unavailable, null, "A pending authority-ledger successor could not be reconciled safely.");
        }

        if (loaded.Document is null)
        {
            return new AuthorityProfileReadResult(AuthorityProfileReadStatus.Unavailable, null, "No trustworthy authority-profile state is available.");
        }

        var profile = loaded.Document.Profiles.SingleOrDefault(value => string.Equals(value.ProfileId, profileId.Value, StringComparison.Ordinal));
        if (profile is null)
        {
            return new AuthorityProfileReadResult(AuthorityProfileReadStatus.NotFound, null, "The authority profile does not exist.");
        }

        return new AuthorityProfileReadResult(loaded.Recovered ? AuthorityProfileReadStatus.RecoveredLastProved : AuthorityProfileReadStatus.Available, MapRecord(loaded.Document, profile), loaded.Recovered ? "The primary authority profile artifact was unsafe; the last proved state is read-only." : "The current authority profile is available.");
    }

    /// <inheritdoc />
    public async Task<AuthorityProfileMutationResult> MutateAsync(AuthorityProfileMutation mutation, CancellationToken cancellationToken = default)
    {
        var validation = ValidateMutation(mutation);
        if (validation is not null)
        {
            return Result(AuthorityProfileMutationStatus.Invalid, mutation?.OperationId ?? string.Empty, null, validation);
        }

        try
        {
            return await _authorityTransaction.ExecuteAsync(token => MutateCoreAsync(mutation, token), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return Result(AuthorityProfileMutationStatus.Unavailable, mutation.OperationId, null, "The authority-profile mutation outcome could not be established safely.");
        }
    }

    private async Task<AuthorityProfileMutationResult> MutateCoreAsync(AuthorityProfileMutation mutation, CancellationToken cancellationToken)
    {
        await using var session = await AcquireLockAsync(cancellationToken);
        var identity = CreateWorkspaceIdentity(session.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
        var loaded = await LoadAsync(session, identity, trust, cancellationToken);
        if (loaded.Document is null || loaded.Recovered)
        {
            return Result(AuthorityProfileMutationStatus.Unavailable, mutation.OperationId, null, "Mutation requires the current proved authority-profile state.");
        }

        var current = loaded.Document;
        var requestHash = ComputeRequestHash(mutation);
        if (current.GrantOperations.Any(value => string.Equals(value.OperationId, mutation.OperationId, StringComparison.Ordinal)))
        {
            return Result(AuthorityProfileMutationStatus.Conflict, mutation.OperationId, null, "The workspace-global operation id is already bound to authority-grant lifecycle intent.");
        }

        var receipt = current.Operations.SingleOrDefault(value => string.Equals(value.OperationId, mutation.OperationId, StringComparison.Ordinal));
        if (receipt is not null)
        {
            if (!string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
            {
                return Result(AuthorityProfileMutationStatus.Conflict, mutation.OperationId, null, "The operation id is already bound to different lifecycle intent.");
            }

            var replayProfile = current.Profiles.SingleOrDefault(value => string.Equals(value.ProfileId, receipt.ProfileId, StringComparison.Ordinal));
            return Result(AuthorityProfileMutationStatus.Replayed, mutation.OperationId, replayProfile is null ? null : MapRecord(current, replayProfile, receipt), "Replayed immutable durable operation evidence.");
        }

        if (current.Operations.Count >= AuthorityProfileStoreLimits.MaximumOperationReceipts)
        {
            return Result(AuthorityProfileMutationStatus.Unavailable, mutation.OperationId, null, "The immutable operation evidence quota is exhausted; no receipt was evicted.");
        }

        var evaluation = EvaluateTransition(current, mutation);
        if (evaluation.Status != AuthorityProfileMutationStatus.Applied)
        {
            return Result(evaluation.Status, mutation.OperationId, evaluation.Profile is null ? null : MapRecord(current, evaluation.Profile), evaluation.Detail);
        }

        if (!TryGetTrustedUtcNow(out var recordedAtUtc))
        {
            return Result(AuthorityProfileMutationStatus.Unavailable, mutation.OperationId, evaluation.Profile is null ? null : MapRecord(current, evaluation.Profile), "The trusted authority-profile operation time is unavailable.");
        }

        if (evaluation.Profile?.Revisions[^1].RecordedAtUtc > recordedAtUtc)
        {
            return Result(AuthorityProfileMutationStatus.Unavailable, mutation.OperationId, MapRecord(current, evaluation.Profile), "The trusted authority-profile operation time precedes retained immutable evidence.");
        }

        var transition = ApplyTransition(current, mutation, recordedAtUtc);
        var profile = transition.Profile!;
        var operation = new AuthorityProfileOperationDocument(mutation.OperationId, requestHash, mutation.Kind, AuthorityProfileMutationStatus.Applied, profile.ProfileId, transition.ResultingRevision, mutation.ActorId.Value, mutation.Reason.Value, recordedAtUtc);
        var profiles = current.Profiles.Where(value => !string.Equals(value.ProfileId, profile.ProfileId, StringComparison.Ordinal)).Append(profile).OrderBy(value => value.ProfileId, StringComparer.Ordinal).ToArray();
        var candidate = new AuthorityProfileStoreDocument(
            AuthorityProfileStoreDocument.CurrentSchemaVersion,
            identity,
            checked(current.Generation + 1),
            profiles,
            current.Operations.Append(operation).OrderBy(value => value.OperationId, StringComparer.Ordinal).ToArray(),
            current.Grants,
            current.GrantOperations,
            string.Empty,
            string.Empty);
        await CommitAsync(session, identity, current, candidate, trust, cancellationToken);
        return Result(AuthorityProfileMutationStatus.Applied, mutation.OperationId, MapRecord(candidate, profile), transition.Detail);
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantStoreReadResult> ReadAsync(AuthorityGrantId grantId, CancellationToken cancellationToken = default)
    {
        if (grantId is null)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        }

        AuthorityGrantStoreReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadGrantCoreAsync(grantId, token);
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
            return callbackResult ?? GrantReadResult(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantStoreReadResult> ReadForMutationAsync(
        AuthorityGrantId grantId,
        string operationId,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        if (grantId is null || !IsOperationIdValid(operationId) || !IsEvidenceHashValid(requestHash))
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        }

        AuthorityGrantStoreReadResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await ReadGrantForMutationCoreAsync(grantId, operationId, token);
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
            return callbackResult ?? GrantReadResult(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        }
    }

    /// <inheritdoc />
    public async Task<AuthorityGrantStoreCommitResult> CommitAsync(AuthorityGrantStoreMutation mutation, CancellationToken cancellationToken = default)
    {
        if (!IsGrantStoreMutationValid(mutation))
        {
            return GrantCommitResult(AuthorityGrantStoreCommitStatus.Unavailable, 0, null, null);
        }

        AuthorityGrantStoreCommitResult? callbackResult = null;
        try
        {
            return await _authorityTransaction.ExecuteAsync(
                async token =>
                {
                    callbackResult = await CommitGrantCoreAsync(mutation, token);
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
            return callbackResult ?? GrantCommitResult(AuthorityGrantStoreCommitStatus.Unavailable, 0, null, null);
        }
    }

    private async Task<AuthorityGrantStoreReadResult> ReadGrantCoreAsync(AuthorityGrantId grantId, CancellationToken cancellationToken)
    {
        await using var session = await AcquireLockAsync(cancellationToken);
        var identity = CreateWorkspaceIdentity(session.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
        var loaded = await LoadAsync(session, identity, trust, cancellationToken);
        if (loaded.Ambiguous)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Ambiguous, 0, null, null);
        }

        if (loaded.Document is null)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        }

        var document = loaded.Document;
        var grant = FindGrant(document, grantId);
        var snapshot = grant is null ? null : MapGrantSnapshot(document, grant);
        if (loaded.Recovered)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Ambiguous, document.Generation, snapshot, null);
        }

        return GrantReadResult(grant is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready, document.Generation, snapshot, null);
    }

    private async Task<AuthorityGrantStoreReadResult> ReadGrantForMutationCoreAsync(
        AuthorityGrantId grantId,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var session = await AcquireLockAsync(cancellationToken);
        var identity = CreateWorkspaceIdentity(session.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
        var loaded = await LoadAsync(session, identity, trust, cancellationToken);
        if (loaded.Ambiguous)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Ambiguous, 0, null, null);
        }

        if (loaded.Document is null)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Unavailable, 0, null, null);
        }

        var document = loaded.Document;
        var grant = FindGrant(document, grantId);
        var snapshot = grant is null ? null : MapGrantSnapshot(document, grant);
        if (loaded.Recovered)
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.Ambiguous, document.Generation, snapshot, null);
        }

        if (document.Operations.Any(operation => string.Equals(operation.OperationId, operationId, StringComparison.Ordinal)))
        {
            return GrantReadResult(AuthorityGrantStoreReadStatus.OperationConflict, document.Generation, snapshot, null);
        }

        var existing = document.GrantOperations.SingleOrDefault(operation => string.Equals(operation.OperationId, operationId, StringComparison.Ordinal));
        if (existing is not null)
        {
            var stored = MapGrantStoredOperation(existing);
            var operationGrant = FindGrant(document, stored.GrantId);
            if (stored.GrantId.Equals(grantId))
            {
                var operationSnapshot = operationGrant is null ? null : MapGrantSnapshot(document, operationGrant, existing);
                return GrantReadResult(
                    operationSnapshot is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready,
                    document.Generation,
                    operationSnapshot,
                    stored);
            }

            return GrantReadResult(
                grant is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready,
                document.Generation,
                snapshot,
                stored);
        }

        return GrantReadResult(grant is null ? AuthorityGrantStoreReadStatus.NotFound : AuthorityGrantStoreReadStatus.Ready, document.Generation, snapshot, null);
    }

    private async Task<AuthorityGrantStoreCommitResult> CommitGrantCoreAsync(AuthorityGrantStoreMutation mutation, CancellationToken cancellationToken)
    {
        var durableIntentStarted = false;
        try
        {
            await using var session = await AcquireLockAsync(cancellationToken);
            var identity = CreateWorkspaceIdentity(session.PhysicalIdentityMaterial);
            var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
            var loaded = await LoadAsync(session, identity, trust, cancellationToken);
            if (loaded.Ambiguous)
            {
                return GrantCommitResult(AuthorityGrantStoreCommitStatus.Ambiguous, 0, null, null);
            }

            if (loaded.Document is null || loaded.Recovered)
            {
                return GrantCommitResult(loaded.Document is null ? AuthorityGrantStoreCommitStatus.Unavailable : AuthorityGrantStoreCommitStatus.Ambiguous, loaded.Document?.Generation ?? 0, null, null);
            }

            var current = loaded.Document;
            var operation = mutation.Operation;
            var currentGrant = FindGrant(current, operation.GrantId);
            AuthorityGrant? currentGrantRevision = null;
            if (currentGrant is not null)
            {
                _ = TryMapGrant(currentGrant, out var currentGrantRevisions);
                currentGrantRevision = currentGrantRevisions[^1];
            }

            if (current.Operations.Any(value => string.Equals(value.OperationId, operation.OperationId, StringComparison.Ordinal)))
            {
                return GrantCommitResult(AuthorityGrantStoreCommitStatus.OperationConflict, current.Generation, null, currentGrant is null ? null : MapGrantSnapshot(current, currentGrant));
            }

            var existingOperation = current.GrantOperations.SingleOrDefault(value => string.Equals(value.OperationId, operation.OperationId, StringComparison.Ordinal));
            if (existingOperation is not null)
            {
                var stored = MapGrantStoredOperation(existingOperation);
                var existingGrant = FindGrant(current, stored.GrantId);
                var sameGrant = stored.GrantId.Equals(operation.GrantId);
                if (!string.Equals(existingOperation.RequestHash, operation.RequestHash, StringComparison.Ordinal)
                    || !sameGrant)
                {
                    var conflictSnapshot = sameGrant
                        ? existingGrant is null ? null : MapGrantSnapshot(current, existingGrant, existingOperation)
                        : currentGrant is null ? null : MapGrantSnapshot(current, currentGrant);
                    return GrantCommitResult(AuthorityGrantStoreCommitStatus.OperationConflict, current.Generation, stored, conflictSnapshot);
                }

                return GrantCommitResult(AuthorityGrantStoreCommitStatus.Replayed, current.Generation, stored, existingGrant is null ? null : MapGrantSnapshot(current, existingGrant, existingOperation));
            }

            if (mutation.ExpectedStoreGeneration != current.Generation)
            {
                return GrantCommitResult(AuthorityGrantStoreCommitStatus.StoreConflict, current.Generation, null, currentGrant is null ? null : MapGrantSnapshot(current, currentGrant));
            }

            if (current.GrantOperations.Count >= AuthorityProfileStoreLimits.MaximumGrantOperationReceipts)
            {
                return GrantCommitResult(AuthorityGrantStoreCommitStatus.LimitExceeded, current.Generation, null, currentGrant is null ? null : MapGrantSnapshot(current, currentGrant));
            }

            if (current.GrantOperations.Count > 0 && operation.RecordedAtUtc < current.GrantOperations[^1].RecordedAtUtc)
            {
                return GrantCommitResult(AuthorityGrantStoreCommitStatus.StoreConflict, current.Generation, null, currentGrant is null ? null : MapGrantSnapshot(current, currentGrant));
            }

            var transitionStatus = mutation.GrantToAppend is null
                ? ValidateReceiptOnlyAppend(mutation.Operation, currentGrantRevision, true)
                : ValidateGrantAppend(current, currentGrant, mutation);
            if (transitionStatus != AuthorityGrantStoreCommitStatus.Committed)
            {
                return GrantCommitResult(transitionStatus, current.Generation, null, currentGrant is null ? null : MapGrantSnapshot(current, currentGrant));
            }

            AuthorityGrantDocument? changedGrant = currentGrant;
            var grants = current.Grants;
            if (mutation.GrantToAppend is not null)
            {
                var revision = NewGrantRevision(mutation.GrantToAppend, operation.OperationId);
                if (currentGrant is null)
                {
                    if (current.Grants.Count >= AuthorityProfileStoreLimits.MaximumGrants)
                    {
                        return GrantCommitResult(AuthorityGrantStoreCommitStatus.LimitExceeded, current.Generation, null, null);
                    }

                    changedGrant = new AuthorityGrantDocument(mutation.GrantToAppend.GrantId.Value, [revision]);
                }
                else
                {
                    if (currentGrant.Revisions.Count >= AuthorityProfileStoreLimits.MaximumRevisionsPerGrant)
                    {
                        return GrantCommitResult(AuthorityGrantStoreCommitStatus.LimitExceeded, current.Generation, null, MapGrantSnapshot(current, currentGrant));
                    }

                    changedGrant = currentGrant with { Revisions = currentGrant.Revisions.Append(revision).ToArray() };
                }

                grants = current.Grants
                    .Where(value => !string.Equals(value.GrantId, changedGrant.GrantId, StringComparison.Ordinal))
                    .Append(changedGrant)
                    .OrderBy(value => value.GrantId, StringComparer.Ordinal)
                    .ToArray();
            }

            var operationDocument = ToGrantOperationDocument(operation);
            var candidate = new AuthorityProfileStoreDocument(
                AuthorityProfileStoreDocument.CurrentSchemaVersion,
                identity,
                checked(current.Generation + 1),
                current.Profiles,
                current.Operations,
                grants,
                current.GrantOperations.Append(operationDocument).ToArray(),
                string.Empty,
                string.Empty);
            if (WouldExceedArtifactLimit(candidate))
            {
                return GrantCommitResult(AuthorityGrantStoreCommitStatus.LimitExceeded, current.Generation, null, currentGrant is null ? null : MapGrantSnapshot(current, currentGrant));
            }

            durableIntentStarted = true;
            await CommitAsync(session, identity, current, candidate, trust, cancellationToken);
            return GrantCommitResult(
                AuthorityGrantStoreCommitStatus.Committed,
                candidate.Generation,
                MapGrantStoredOperation(operationDocument),
                changedGrant is null ? null : MapGrantSnapshot(candidate, changedGrant, operationDocument));
        }
        catch (OperationCanceledException) when (durableIntentStarted)
        {
            return GrantCommitResult(AuthorityGrantStoreCommitStatus.Ambiguous, 0, null, null);
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return GrantCommitResult(durableIntentStarted ? AuthorityGrantStoreCommitStatus.Ambiguous : AuthorityGrantStoreCommitStatus.Unavailable, 0, null, null);
        }
    }

    private static Transition EvaluateTransition(AuthorityProfileStoreDocument current, AuthorityProfileMutation mutation)
    {
        var targetId = mutation.Profile?.ProfileId.Value ?? mutation.ProfileId!.Value;
        var existing = current.Profiles.SingleOrDefault(value => string.Equals(value.ProfileId, targetId, StringComparison.Ordinal));
        if (mutation.Kind == AuthorityProfileMutationKind.Create)
        {
            if (existing is not null)
            {
                return new Transition(AuthorityProfileMutationStatus.Invalid, existing, null, "A profile declaration or retained tombstone already uses this identifier.");
            }

            if (current.Profiles.Count >= AuthorityProfileStoreLimits.MaximumProfiles)
            {
                return new Transition(AuthorityProfileMutationStatus.Unavailable, null, null, "The bounded profile quota is exhausted.");
            }

            return new Transition(AuthorityProfileMutationStatus.Applied, null, null, "The non-self-granting profile declaration can be retained.");
        }

        if (existing is null)
        {
            return new Transition(AuthorityProfileMutationStatus.NotFound, null, null, "The authority profile does not exist.");
        }

        if (existing.Tombstone is not null)
        {
            return new Transition(AuthorityProfileMutationStatus.Invalid, existing, null, "A retained authority-profile tombstone cannot be changed or resurrected.");
        }

        var latest = existing.Revisions[^1];
        if (mutation.ExpectedRevision != latest.Revision)
        {
            return new Transition(AuthorityProfileMutationStatus.Conflict, existing, null, "The expected authority-profile revision is stale.");
        }

        if (mutation.Kind == AuthorityProfileMutationKind.Tombstone)
        {
            return new Transition(AuthorityProfileMutationStatus.Applied, existing, null, "The profile tombstone can be retained without rewriting profile history.");
        }

        if (existing.Revisions.Count >= AuthorityProfileStoreLimits.MaximumRevisionsPerProfile)
        {
            return new Transition(AuthorityProfileMutationStatus.Unavailable, existing, null, "The immutable profile revision quota is exhausted; no revision or receipt was written.");
        }

        return new Transition(AuthorityProfileMutationStatus.Applied, existing, null, "The immutable successor profile revision can be retained.");
    }

    private static Transition ApplyTransition(AuthorityProfileStoreDocument current, AuthorityProfileMutation mutation, DateTimeOffset recordedAtUtc)
    {
        var targetId = mutation.Profile?.ProfileId.Value ?? mutation.ProfileId!.Value;
        var existing = current.Profiles.SingleOrDefault(value => string.Equals(value.ProfileId, targetId, StringComparison.Ordinal));
        if (mutation.Kind == AuthorityProfileMutationKind.Create)
        {
            var revision = NewRevision(mutation.Profile!, mutation.OperationId, recordedAtUtc);
            return new Transition(AuthorityProfileMutationStatus.Applied, new AuthorityProfileDocument(targetId, [revision], null), revision.Revision, "The non-self-granting profile declaration was retained.");
        }

        if (mutation.Kind == AuthorityProfileMutationKind.Tombstone)
        {
            var tombstone = new AuthorityProfileTombstoneDocument(mutation.OperationId, mutation.ActorId.Value, mutation.Reason.Value, recordedAtUtc);
            return new Transition(AuthorityProfileMutationStatus.Applied, existing! with { Tombstone = tombstone }, null, "The profile tombstone was retained without rewriting profile history.");
        }

        var latest = existing!.Revisions[^1];
        AuthorityProfile successor;
        if (mutation.Kind == AuthorityProfileMutationKind.Revise)
        {
            successor = mutation.Profile!;
        }
        else
        {
            _ = AuthorityProfileJson.TryDeserialize(latest.ProfileJson, out var previous, out _);
            _ = AuthorityProfileRevision.TryParse(checked(latest.Revision + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), out var revision, out _);
            successor = previous! with { Revision = revision!, Status = mutation.Status!.Value };
        }

        var appended = NewRevision(successor, mutation.OperationId, recordedAtUtc);
        return new Transition(AuthorityProfileMutationStatus.Applied, existing with { Revisions = existing.Revisions.Append(appended).ToArray() }, appended.Revision, mutation.Kind == AuthorityProfileMutationKind.Revise ? "The immutable successor profile revision was retained." : "The immutable successor status snapshot was retained.");
    }

    private async Task<LoadResult> LoadAsync(CapabilityCatalogPathSession session, string identity, CapabilityCatalogTrustState? trust, CancellationToken cancellationToken)
    {
        var primaryExists = session.FileExists(_paths.AuthorityProfilesDocumentPath);
        var proofExists = session.FileExists(_paths.AuthorityProfilesProofPath);
        var empty = EmptyDocument(identity);
        if (trust is null)
        {
            return primaryExists || proofExists ? new LoadResult(null, false) : new LoadResult(empty, false);
        }

        if (!primaryExists && !proofExists)
        {
            return MatchesCurrent(empty, trust) ? new LoadResult(empty, false) : new LoadResult(null, false);
        }

        var primary = primaryExists ? await TryReadAsync(session, identity, _paths.AuthorityProfilesDocumentPath, cancellationToken) : null;
        var proof = proofExists ? await TryReadAsync(session, identity, _paths.AuthorityProfilesProofPath, cancellationToken) : null;
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return new LoadResult(primary, false);
        }

        if (primary is not null
            && proof is not null
            && MatchesCurrent(proof, trust)
            && IsGrantDirectSuccessor(proof, primary))
        {
            try
            {
                _ = await _trustProvider.AdvanceAsync(identity, trust.CurrentGeneration, trust.CurrentContentDigest, primary.Generation, primary.ContentDigest, cancellationToken);
                return new LoadResult(primary, false);
            }
            catch (OperationCanceledException)
            {
                return new LoadResult(null, false, true);
            }
            catch (Exception exception) when (IsAvailabilityFailure(exception))
            {
                return new LoadResult(null, false, true);
            }
        }

        if (proof is not null && (MatchesCurrent(proof, trust) || MatchesPrevious(proof, trust)))
        {
            return new LoadResult(proof, true);
        }

        return primary is not null && MatchesPrevious(primary, trust) ? new LoadResult(primary, true) : new LoadResult(null, false);
    }

    private static bool IsGrantDirectSuccessor(AuthorityProfileStoreDocument current, AuthorityProfileStoreDocument candidate)
    {
        if (candidate.Generation != current.Generation + 1
            || !EquivalentJson(current.Profiles, candidate.Profiles)
            || !EquivalentJson(current.Operations, candidate.Operations)
            || candidate.GrantOperations.Count != current.GrantOperations.Count + 1
            || !EquivalentJson(current.GrantOperations, candidate.GrantOperations.Take(current.GrantOperations.Count).ToArray()))
        {
            return false;
        }

        var operation = candidate.GrantOperations[^1];
        if (operation.Outcome != AuthorityGrantOperationOutcome.Committed)
        {
            return operation.ResultingGrantId is null
                && operation.ResultingGrantRevision is null
                && operation.ResultingGrantContentHash is null
                && EquivalentJson(current.Grants, candidate.Grants);
        }

        var currentGrant = current.Grants.SingleOrDefault(value => string.Equals(value.GrantId, operation.GrantId, StringComparison.Ordinal));
        var candidateGrant = candidate.Grants.SingleOrDefault(value => string.Equals(value.GrantId, operation.GrantId, StringComparison.Ordinal));
        if (candidateGrant is null
            || candidate.Grants.Count != current.Grants.Count + (currentGrant is null ? 1 : 0)
            || operation.ResultingGrantRevision != candidateGrant.Revisions[^1].Revision
            || !string.Equals(operation.ResultingGrantContentHash, candidateGrant.Revisions[^1].ContentHash, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var grant in current.Grants)
        {
            var successor = candidate.Grants.SingleOrDefault(value => string.Equals(value.GrantId, grant.GrantId, StringComparison.Ordinal));
            if (successor is null)
            {
                return false;
            }

            if (!string.Equals(grant.GrantId, operation.GrantId, StringComparison.Ordinal))
            {
                if (!EquivalentJson(grant, successor))
                {
                    return false;
                }

                continue;
            }

            if (successor.Revisions.Count != grant.Revisions.Count + 1
                || !EquivalentJson(grant.Revisions, successor.Revisions.Take(grant.Revisions.Count).ToArray()))
            {
                return false;
            }
        }

        return currentGrant is not null || candidateGrant.Revisions.Count == 1;
    }

    private static bool EquivalentJson<T>(T left, T right)
        => string.Equals(JsonSerializer.Serialize(left, _hashOptions), JsonSerializer.Serialize(right, _hashOptions), StringComparison.Ordinal);

    private bool WouldExceedArtifactLimit(AuthorityProfileStoreDocument document)
    {
        var withoutProof = JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _jsonOptions) + Environment.NewLine;
        var maximumEscapedAuthenticationTagBytes = checked(_trustProvider.MaximumAuthenticationTagUtf8Bytes * 6);
        return Encoding.UTF8.GetByteCount(withoutProof) + 64 + maximumEscapedAuthenticationTagBytes > AuthorityProfileStoreLimits.MaximumArtifactUtf8Bytes;
    }

    private async Task<AuthorityProfileStoreDocument?> TryReadAsync(CapabilityCatalogPathSession session, string identity, string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.ReadAllBytesAsync(path, AuthorityProfileStoreLimits.MaximumArtifactUtf8Bytes, cancellationToken);
            using var parsed = JsonDocument.Parse(_strictUtf8.GetString(bytes), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            if (HasDuplicateProperties(parsed.RootElement))
            {
                return null;
            }

            var document = parsed.RootElement.Deserialize<AuthorityProfileStoreDocument>(_jsonOptions);
            return document is not null && ValidateDocument(document, identity) && await _trustProvider.VerifyArtifactAsync(identity, document.Generation, document.ContentDigest, document.AuthenticationTag, cancellationToken) ? document : null;
        }
        catch (JsonException)
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

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (HasDuplicateProperties(item))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task CommitAsync(CapabilityCatalogPathSession session, string identity, AuthorityProfileStoreDocument current, AuthorityProfileStoreDocument candidate, CapabilityCatalogTrustState? trust, CancellationToken cancellationToken)
    {
        var currentDigest = ComputeContentDigest(current).Value;
        trust ??= await _trustProvider.InitializeAsync(identity, current.Generation, currentDigest, cancellationToken);
        if (!MatchesCurrent(current with { ContentDigest = currentDigest }, trust))
        {
            throw new IOException("The server-owned authority-profile proof no longer matches the mutation base.");
        }

        var currentJson = await SerializeAsync(identity, current, cancellationToken);
        await session.WriteTextAtomicallyAsync(_paths.AuthorityProfilesProofPath, currentJson.Json, cancellationToken);
        var candidateJson = await SerializeAsync(identity, candidate, cancellationToken);
        await session.WriteTextAtomicallyAsync(_paths.AuthorityProfilesDocumentPath, candidateJson.Json, cancellationToken);
        _ = await _trustProvider.AdvanceAsync(identity, trust.CurrentGeneration, trust.CurrentContentDigest, candidate.Generation, candidateJson.ContentDigest, cancellationToken);
    }

    private async Task<SerializedDocument> SerializeAsync(string identity, AuthorityProfileStoreDocument document, CancellationToken cancellationToken)
    {
        var digest = ComputeContentDigest(document).Value;
        var tag = await _trustProvider.AuthenticateArtifactAsync(identity, document.Generation, digest, cancellationToken);
        var json = JsonSerializer.Serialize(document with { ContentDigest = digest, AuthenticationTag = tag }, _jsonOptions) + Environment.NewLine;
        if (Encoding.UTF8.GetByteCount(json) > AuthorityProfileStoreLimits.MaximumArtifactUtf8Bytes)
        {
            throw new IOException("The bounded authority-profile artifact limit would be exceeded.");
        }

        return new SerializedDocument(json, digest);
    }

    private bool ValidateDocument(AuthorityProfileStoreDocument document, string identity)
    {
        if (document.SchemaVersion != AuthorityProfileStoreDocument.CurrentSchemaVersion
            || !string.Equals(document.WorkspaceIdentity, identity, StringComparison.Ordinal)
            || document.Generation < 0
            || document.Profiles is null
            || document.Operations is null
            || document.Grants is null
            || document.GrantOperations is null
            || document.Profiles.Count > AuthorityProfileStoreLimits.MaximumProfiles
            || document.Operations.Count > AuthorityProfileStoreLimits.MaximumOperationReceipts
            || document.Grants.Count > AuthorityProfileStoreLimits.MaximumGrants
            || document.GrantOperations.Count > AuthorityProfileStoreLimits.MaximumGrantOperationReceipts
            || document.Generation != document.Operations.Count + (long)document.GrantOperations.Count
            || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var supplied, out _)
            || !supplied!.FixedTimeEquals(ComputeContentDigest(document)))
        {
            return false;
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in document.Profiles)
        {
            if (!TryMapProfile(profile, out _) || !profileIds.Add(profile.ProfileId))
            {
                return false;
            }
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var operation in document.Operations)
        {
            if (operation is null || !IsOperationIdValid(operation.OperationId) || !operationIds.Add(operation.OperationId) || !CapabilityIntegrityDigest.TryParse(operation.RequestHash, out _, out _) || operation.Outcome != AuthorityProfileMutationStatus.Applied || !Enum.IsDefined(operation.Kind) || operation.Kind == AuthorityProfileMutationKind.Tombstone && operation.ResultingRevision is not null || operation.Kind != AuthorityProfileMutationKind.Tombstone && operation.ResultingRevision is null || !AuthorityProfileId.TryParse(operation.ProfileId, out _, out _) || !AuthorityActorId.TryParse(operation.ActorId, out _, out _) || !AuthorityPurpose.TryParse(operation.Reason, out _, out _) || operation.RecordedAtUtc == default || operation.RecordedAtUtc.Offset != TimeSpan.Zero)
            {
                return false;
            }
        }

        if (!ValidateProfileOperationLineage(document.Profiles, document.Operations))
        {
            return false;
        }

        var grantIds = new HashSet<string>(StringComparer.Ordinal);
        var mappedGrants = new Dictionary<string, IReadOnlyList<AuthorityGrant>>(StringComparer.Ordinal);
        foreach (var grant in document.Grants)
        {
            if (!TryMapGrant(grant, out var revisions) || !grantIds.Add(grant.GrantId))
            {
                return false;
            }

            mappedGrants.Add(grant.GrantId, revisions);
        }

        var mappedGrantOperations = new Dictionary<string, AuthorityGrantOperationEvidence>(StringComparer.Ordinal);
        DateTimeOffset? previousGrantOperationTime = null;
        foreach (var operation in document.GrantOperations)
        {
            if (!TryMapGrantOperation(operation, out var mapped)
                || !operationIds.Add(operation.OperationId)
                || previousGrantOperationTime is { } previousTime && mapped!.RecordedAtUtc < previousTime)
            {
                return false;
            }

            mappedGrantOperations.Add(operation.OperationId, mapped!);
            previousGrantOperationTime = mapped!.RecordedAtUtc;
        }

        var observedRevisionCounts = mappedGrants.Keys.ToDictionary(key => key, _ => 0, StringComparer.Ordinal);
        foreach (var operationDocument in document.GrantOperations)
        {
            var operation = mappedGrantOperations[operationDocument.OperationId];
            if (operation.Outcome != AuthorityGrantOperationOutcome.Committed)
            {
                AuthorityGrant? historicalGrant = null;
                if (mappedGrants.TryGetValue(operation.GrantId.Value, out var historicalRevisions)
                    && observedRevisionCounts.TryGetValue(operation.GrantId.Value, out var observedRevisions)
                    && observedRevisions > 0)
                {
                    historicalGrant = historicalRevisions[observedRevisions - 1];
                }

                if (ValidateReceiptOnlyAppend(operation, historicalGrant, true) != AuthorityGrantStoreCommitStatus.Committed)
                {
                    return false;
                }

                continue;
            }

            if (!mappedGrants.TryGetValue(operation.GrantId.Value, out var revisions))
            {
                return false;
            }

            var revisionIndex = observedRevisionCounts[operation.GrantId.Value];
            if (revisionIndex >= revisions.Count)
            {
                return false;
            }

            var revision = revisions[revisionIndex];
            var persistedRevision = document.Grants.Single(value => string.Equals(value.GrantId, operation.GrantId.Value, StringComparison.Ordinal)).Revisions[revisionIndex];
            var previous = revisionIndex == 0 ? null : revisions[revisionIndex - 1];
            if (!string.Equals(persistedRevision.OperationId, operation.OperationId, StringComparison.Ordinal)
                || operation.ResultingGrant is null
                || !operation.ResultingGrant.GrantId.Equals(revision.GrantId)
                || !operation.ResultingGrant.Revision.Equals(revision.Revision)
                || !string.Equals(operation.ResultingGrant.ContentHash, revision.ContentHash, StringComparison.Ordinal)
                || !operation.ActorId.Equals(revision.ChangedByActorId)
                || !operation.Reason.Equals(revision.Reason)
                || operation.RecordedAtUtc != revision.RecordedAtUtc
                || previous is null && (operation.Kind != AuthorityGrantOperationKind.Create || operation.ExpectedRevision != 0 || revision.Status != AuthorityGrantLifecycleStatus.Active)
                || previous is not null && !AuthorityGrantContractValidator.ValidateTransition(previous, revision, operation.Kind).IsValid)
            {
                return false;
            }

            observedRevisionCounts[operation.GrantId.Value] = revisionIndex + 1;
        }

        if (mappedGrants.Any(pair => observedRevisionCounts[pair.Key] != pair.Value.Count))
        {
            return false;
        }

        return document.Profiles.Select(value => value.ProfileId).SequenceEqual(document.Profiles.Select(value => value.ProfileId).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            && document.Operations.Select(value => value.OperationId).SequenceEqual(document.Operations.Select(value => value.OperationId).Order(StringComparer.Ordinal), StringComparer.Ordinal)
            && document.Grants.Select(value => value.GrantId).SequenceEqual(document.Grants.Select(value => value.GrantId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool TryMapProfile(AuthorityProfileDocument document, out AuthorityProfileRecord? record)
    {
        record = null;
        if (document is null || !AuthorityProfileId.TryParse(document.ProfileId, out var id, out _) || document.Revisions is null || document.Revisions.Count is < 1 or > AuthorityProfileStoreLimits.MaximumRevisionsPerProfile || document.Tombstone is not null && !TryMapTombstone(document.Tombstone, out _))
        {
            return false;
        }

        var revisions = new List<AuthorityProfileRevisionEvidence>();
        var expected = 1;
        foreach (var revision in document.Revisions)
        {
            if (revision.Revision != expected++ || !IsOperationIdValid(revision.OperationId) || revision.RecordedAtUtc == default || revision.RecordedAtUtc.Offset != TimeSpan.Zero || !AuthorityProfileJson.TryDeserialize(revision.ProfileJson, out var profile, out _) || !string.Equals(profile!.ProfileId.Value, id!.Value, StringComparison.Ordinal) || profile.Revision.Value != revision.Revision || !AuthorityProfileHash.TryCompute(profile, out var hash, out _) || !string.Equals(hash!.Value, revision.ProfileHash, StringComparison.Ordinal))
            {
                return false;
            }

            revisions.Add(new AuthorityProfileRevisionEvidence(profile, hash!, revision.OperationId, revision.RecordedAtUtc));
        }

        record = new AuthorityProfileRecord(id!, revisions[^1].Profile, revisions[^1].Hash, revisions, document.Tombstone is null ? null : MapTombstone(document.Tombstone), []);
        return true;
    }

    private static bool ValidateProfileOperationLineage(
        IReadOnlyList<AuthorityProfileDocument> profiles,
        IReadOnlyList<AuthorityProfileOperationDocument> operations)
    {
        var profilesById = profiles.ToDictionary(profile => profile.ProfileId, StringComparer.Ordinal);
        var operationsById = operations.ToDictionary(operation => operation.OperationId, StringComparer.Ordinal);
        var operationCountsByProfile = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (!profilesById.ContainsKey(operation.ProfileId))
            {
                return false;
            }

            operationCountsByProfile[operation.ProfileId] = operationCountsByProfile.GetValueOrDefault(operation.ProfileId) + 1;
        }

        foreach (var profile in profiles)
        {
            var expectedOperationCount = profile.Revisions.Count + (profile.Tombstone is null ? 0 : 1);
            if (operationCountsByProfile.GetValueOrDefault(profile.ProfileId) != expectedOperationCount)
            {
                return false;
            }

            DateTimeOffset? previousRevisionTime = null;
            for (var index = 0; index < profile.Revisions.Count; index++)
            {
                var revision = profile.Revisions[index];
                var expectedKind = index == 0 ? AuthorityProfileMutationKind.Create : (AuthorityProfileMutationKind?)null;
                if (previousRevisionTime is { } previous && revision.RecordedAtUtc < previous
                    || !operationsById.TryGetValue(revision.OperationId, out var operation)
                    || !string.Equals(operation.ProfileId, profile.ProfileId, StringComparison.Ordinal)
                    || operation.ResultingRevision != revision.Revision
                    || expectedKind is { } firstKind && operation.Kind != firstKind
                    || expectedKind is null && operation.Kind is not AuthorityProfileMutationKind.Revise and not AuthorityProfileMutationKind.TransitionStatus
                    || operation.Kind == AuthorityProfileMutationKind.TransitionStatus
                        && !IsStatusOnlyProfileSuccessor(profile.Revisions[index - 1], revision)
                    || operation.RecordedAtUtc != revision.RecordedAtUtc)
                {
                    return false;
                }

                previousRevisionTime = revision.RecordedAtUtc;
            }

            if (profile.Tombstone is not { } tombstone)
            {
                continue;
            }

            if (previousRevisionTime is { } latestRevisionTime && tombstone.RecordedAtUtc < latestRevisionTime
                || !operationsById.TryGetValue(tombstone.OperationId, out var tombstoneOperation)
                || !string.Equals(tombstoneOperation.ProfileId, profile.ProfileId, StringComparison.Ordinal)
                || tombstoneOperation.Kind != AuthorityProfileMutationKind.Tombstone
                || tombstoneOperation.ResultingRevision is not null
                || !string.Equals(tombstoneOperation.ActorId, tombstone.ActorId, StringComparison.Ordinal)
                || !string.Equals(tombstoneOperation.Reason, tombstone.Reason, StringComparison.Ordinal)
                || tombstoneOperation.RecordedAtUtc != tombstone.RecordedAtUtc)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStatusOnlyProfileSuccessor(
        AuthorityProfileRevisionDocument previousRevision,
        AuthorityProfileRevisionDocument successorRevision)
    {
        if (!AuthorityProfileJson.TryDeserialize(previousRevision.ProfileJson, out var previous, out _)
            || !AuthorityProfileJson.TryDeserialize(successorRevision.ProfileJson, out var successor, out _))
        {
            return false;
        }

        var expected = previous! with
        {
            Revision = successor!.Revision,
            Status = successor.Status
        };
        return AuthorityProfileHash.TryCompute(expected, out var expectedHash, out _)
            && AuthorityProfileHash.TryCompute(successor, out var successorHash, out _)
            && expectedHash!.Equals(successorHash);
    }

    private AuthorityGrantStoreCommitStatus ValidateGrantAppend(
        AuthorityProfileStoreDocument document,
        AuthorityGrantDocument? currentDocument,
        AuthorityGrantStoreMutation mutation)
    {
        var next = mutation.GrantToAppend!;
        var operation = mutation.Operation;
        if (currentDocument is null)
        {
            if (operation.Kind != AuthorityGrantOperationKind.Create
                || operation.ExpectedRevision != 0
                || next.Revision.Value != 1
                || next.Status != AuthorityGrantLifecycleStatus.Active)
            {
                return AuthorityGrantStoreCommitStatus.StoreConflict;
            }
        }
        else
        {
            if (!TryMapGrant(currentDocument, out var revisions)
                || operation.Kind == AuthorityGrantOperationKind.Create
                || operation.ExpectedRevision != revisions[^1].Revision.Value
                || !AuthorityGrantContractValidator.ValidateTransition(revisions[^1], next, operation.Kind).IsValid)
            {
                return AuthorityGrantStoreCommitStatus.StoreConflict;
            }
        }

        if (operation.Kind is AuthorityGrantOperationKind.Create or AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace
            && !MatchesActiveProfilePin(document, next.Binding.Profile))
        {
            return AuthorityGrantStoreCommitStatus.StoreConflict;
        }

        return AuthorityGrantStoreCommitStatus.Committed;
    }

    private static AuthorityGrantStoreCommitStatus ValidateReceiptOnlyAppend(AuthorityGrantOperationEvidence operation)
        => ValidateReceiptOnlyAppend(operation, null, false);

    private static AuthorityGrantStoreCommitStatus ValidateReceiptOnlyAppend(
        AuthorityGrantOperationEvidence operation,
        AuthorityGrant? target,
        bool stateKnown)
    {
        if (operation.ResultingGrant is not null)
        {
            return AuthorityGrantStoreCommitStatus.Unavailable;
        }

        var exactNonterminalTarget = target is not null
            && operation.ExpectedRevision == target.Revision.Value
            && target.Status is AuthorityGrantLifecycleStatus.Active or AuthorityGrantLifecycleStatus.Suspended;
        return (operation.Outcome, operation.FailureCode) switch
        {
            (AuthorityGrantOperationOutcome.NotFound, AuthorityGrantOperationFailureCode.LifecycleConflict)
                when operation.Kind != AuthorityGrantOperationKind.Create && (!stateKnown || target is null)
                => AuthorityGrantStoreCommitStatus.Committed,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.LifecycleConflict)
                when !stateKnown || target is not null
                => AuthorityGrantStoreCommitStatus.Committed,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.BoundaryConflict)
                when operation.Kind == AuthorityGrantOperationKind.Create && (!stateKnown || target is null)
                    || operation.Kind is AuthorityGrantOperationKind.Replace or AuthorityGrantOperationKind.Expire
                        && (!stateKnown || exactNonterminalTarget)
                => AuthorityGrantStoreCommitStatus.Committed,
            (AuthorityGrantOperationOutcome.Conflict, AuthorityGrantOperationFailureCode.CeilingExceeded)
                when operation.Kind == AuthorityGrantOperationKind.Create && (!stateKnown || target is null)
                    || operation.Kind is AuthorityGrantOperationKind.Narrow or AuthorityGrantOperationKind.Replace
                        && (!stateKnown || exactNonterminalTarget)
                => AuthorityGrantStoreCommitStatus.Committed,
            (AuthorityGrantOperationOutcome.LimitExceeded, AuthorityGrantOperationFailureCode.LimitExceeded)
                => AuthorityGrantStoreCommitStatus.Committed,
            _ => AuthorityGrantStoreCommitStatus.Unavailable,
        };
    }

    private bool MatchesActiveProfilePin(AuthorityProfileStoreDocument document, AuthorityGrantProfilePin profilePin)
    {
        var profileDocument = document.Profiles.SingleOrDefault(value => string.Equals(value.ProfileId, profilePin.Reference.ProfileId.Value, StringComparison.Ordinal));
        if (profileDocument is null
            || profileDocument.Tombstone is not null
            || !TryMapProfile(profileDocument, out var record)
            || !record!.CurrentProfile.ProfileId.Equals(profilePin.Reference.ProfileId)
            || !record.CurrentProfile.Revision.Equals(profilePin.Reference.Revision)
            || !record.CurrentHash.Equals(profilePin.ContentHash)
            || record.CurrentProfile.Status != AuthorityProfileStatus.Active)
        {
            return false;
        }

        var intersection = AuthorityCeilingIntersection.Evaluate([record.CurrentProfile], _timeProvider.GetUtcNow());
        return intersection.Validation.IsValid && intersection.Receipt.Decision == AuthorityBoundaryDecision.Direct;
    }

    private static AuthorityGrantDocument? FindGrant(AuthorityProfileStoreDocument document, AuthorityGrantId grantId)
        => document.Grants.SingleOrDefault(value => string.Equals(value.GrantId, grantId.Value, StringComparison.Ordinal));

    private static AuthorityGrantRevisionDocument NewGrantRevision(AuthorityGrant grant, string operationId)
    {
        if (!AuthorityGrantJson.TrySerialize(grant, out var json, out _))
        {
            throw new FormatException("The authority-grant successor could not be serialized canonically.");
        }

        return new AuthorityGrantRevisionDocument(grant.Revision.Value, json!, grant.ContentHash, operationId, grant.RecordedAtUtc);
    }

    private static bool TryMapGrant(AuthorityGrantDocument document, out IReadOnlyList<AuthorityGrant> revisions)
    {
        revisions = [];
        if (document is null
            || !AuthorityGrantId.TryParse(document.GrantId, out var grantId, out _)
            || document.Revisions is null
            || document.Revisions.Count is < 1 or > AuthorityProfileStoreLimits.MaximumRevisionsPerGrant)
        {
            return false;
        }

        var mapped = new List<AuthorityGrant>(document.Revisions.Count);
        var expectedRevision = 1;
        foreach (var revision in document.Revisions)
        {
            if (revision is null
                || revision.Revision != expectedRevision++
                || !IsOperationIdValid(revision.OperationId)
                || revision.RecordedAtUtc == default
                || revision.RecordedAtUtc.Offset != TimeSpan.Zero
                || !AuthorityGrantJson.TryDeserialize(revision.GrantJson, out var grant, out _)
                || !grant!.GrantId.Equals(grantId)
                || grant.Revision.Value != revision.Revision
                || grant.RecordedAtUtc != revision.RecordedAtUtc
                || !string.Equals(grant.ContentHash, revision.ContentHash, StringComparison.Ordinal))
            {
                revisions = [];
                return false;
            }

            mapped.Add(grant);
        }

        revisions = mapped;
        return true;
    }

    private static AuthorityGrantStoreSnapshot? MapGrantSnapshot(
        AuthorityProfileStoreDocument document,
        AuthorityGrantDocument grantDocument,
        AuthorityGrantOperationDocument? throughOperation = null)
    {
        if (!TryMapGrant(grantDocument, out var allRevisions))
        {
            throw new FormatException("The authority-grant revision lineage is invalid.");
        }

        var operationLimit = document.GrantOperations.Count;
        if (throughOperation is not null)
        {
            operationLimit = -1;
            for (var index = 0; index < document.GrantOperations.Count; index++)
            {
                if (ReferenceEquals(document.GrantOperations[index], throughOperation)
                    || document.GrantOperations[index] == throughOperation)
                {
                    operationLimit = index + 1;
                    break;
                }
            }
        }

        if (operationLimit < 1)
        {
            throw new FormatException("The authority-grant operation is not retained by this ledger.");
        }

        var operations = document.GrantOperations
            .Take(operationLimit)
            .Where(value => string.Equals(value.GrantId, grantDocument.GrantId, StringComparison.Ordinal))
            .Select(value => MapGrantStoredOperation(value).Evidence)
            .ToArray();
        var lastCommitted = operations.LastOrDefault(value => value.Outcome == AuthorityGrantOperationOutcome.Committed);
        if (lastCommitted?.ResultingGrant is null)
        {
            return null;
        }

        var revisionLimit = lastCommitted.ResultingGrant.Revision.Value;
        var revisions = allRevisions.Where(value => value.Revision.Value <= revisionLimit).ToArray();
        if (revisions.Length == 0)
        {
            throw new FormatException("The authority-grant operation does not identify a retained revision.");
        }

        return new AuthorityGrantStoreSnapshot(revisions[^1], revisions, operations);
    }

    private static AuthorityGrantStoredOperation MapGrantStoredOperation(AuthorityGrantOperationDocument document)
    {
        if (!TryMapGrantOperation(document, out var operation))
        {
            throw new FormatException("The authority-grant operation evidence is invalid.");
        }

        return new AuthorityGrantStoredOperation(operation!.GrantId, operation);
    }

    private static bool TryMapGrantOperation(AuthorityGrantOperationDocument document, out AuthorityGrantOperationEvidence? operation)
    {
        operation = null;
        if (document is null
            || !AuthorityGrantId.TryParse(document.GrantId, out var grantId, out _)
            || !AuthorityActorId.TryParse(document.ActorId, out var actorId, out _)
            || !AuthorityPurpose.TryParse(document.Reason, out var reason, out _))
        {
            return false;
        }

        AuthorityGrantReference? resultingGrant = null;
        var hasResultIdentity = document.ResultingGrantId is not null || document.ResultingGrantRevision is not null || document.ResultingGrantContentHash is not null;
        if (hasResultIdentity)
        {
            if (!AuthorityGrantId.TryParse(document.ResultingGrantId, out var resultingGrantId, out _)
                || !AuthorityGrantRevision.TryParse(document.ResultingGrantRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture), out var resultingRevision, out _)
                || document.ResultingGrantContentHash is null)
            {
                return false;
            }

            resultingGrant = new AuthorityGrantReference(resultingGrantId!, resultingRevision!, document.ResultingGrantContentHash);
        }

        operation = new AuthorityGrantOperationEvidence(
            document.SchemaVersion,
            document.OperationId,
            document.RequestHash,
            document.Kind,
            document.Outcome,
            document.FailureCode,
            grantId!,
            document.ExpectedRevision,
            resultingGrant,
            actorId!,
            reason!,
            document.AuthorityEvidenceHash,
            document.DependencyEvidenceHash,
            document.RecordedAtUtc);
        if (!AuthorityGrantContractValidator.Validate(operation).IsValid)
        {
            operation = null;
            return false;
        }

        return true;
    }

    private static AuthorityGrantOperationDocument ToGrantOperationDocument(AuthorityGrantOperationEvidence operation)
    {
        return new AuthorityGrantOperationDocument(
            operation.SchemaVersion,
            operation.OperationId,
            operation.RequestHash,
            operation.Kind,
            operation.Outcome,
            operation.FailureCode,
            operation.GrantId.Value,
            operation.ExpectedRevision,
            operation.ResultingGrant?.GrantId.Value,
            operation.ResultingGrant?.Revision.Value,
            operation.ResultingGrant?.ContentHash,
            operation.ActorId.Value,
            operation.Reason.Value,
            operation.AuthorityEvidenceHash,
            operation.DependencyEvidenceHash,
            operation.RecordedAtUtc);
    }

    private static AuthorityProfileRecord MapRecord(AuthorityProfileStoreDocument document, AuthorityProfileDocument profile, AuthorityProfileOperationDocument? receipt = null)
    {
        if (!TryMapProfile(profile, out var mapped))
        {
            throw new FormatException("The authority-profile record is invalid.");
        }

        var limit = receipt?.ResultingRevision ?? int.MaxValue;
        var operationLimit = receipt?.ResultingRevision ?? (receipt?.Kind == AuthorityProfileMutationKind.Tombstone ? mapped!.CurrentProfile.Revision.Value : null);
        var revisions = mapped!.Revisions.Where(value => value.Profile.Revision.Value <= limit).ToArray();
        var operations = document.Operations.Where(value => string.Equals(value.ProfileId, mapped.ProfileId.Value, StringComparison.Ordinal) && (receipt is null || value.OperationId == receipt.OperationId || operationLimit.HasValue && value.ResultingRevision is int resultingRevision && resultingRevision <= operationLimit.Value)).Select(MapReceipt).ToArray();
        var tombstone = profile.Tombstone is not null && (receipt is null || receipt.Kind == AuthorityProfileMutationKind.Tombstone) ? MapTombstone(profile.Tombstone) : null;
        return new AuthorityProfileRecord(mapped.ProfileId, revisions[^1].Profile, revisions[^1].Hash, revisions, tombstone, operations);
    }

    private static AuthorityProfileOperationReceipt MapReceipt(AuthorityProfileOperationDocument document)
    {
        _ = AuthorityProfileId.TryParse(document.ProfileId, out var profileId, out _);
        _ = AuthorityActorId.TryParse(document.ActorId, out var actorId, out _);
        _ = AuthorityPurpose.TryParse(document.Reason, out var reason, out _);
        return new AuthorityProfileOperationReceipt(document.OperationId, document.RequestHash, document.Kind, document.Outcome, profileId!, document.ResultingRevision, actorId!, reason!, document.RecordedAtUtc);
    }

    private static bool TryMapTombstone(AuthorityProfileTombstoneDocument document, out AuthorityProfileTombstone? tombstone)
    {
        tombstone = null;
        if (!IsOperationIdValid(document.OperationId) || document.RecordedAtUtc == default || document.RecordedAtUtc.Offset != TimeSpan.Zero || !AuthorityActorId.TryParse(document.ActorId, out var actorId, out _) || !AuthorityPurpose.TryParse(document.Reason, out var reason, out _))
        {
            return false;
        }

        tombstone = new AuthorityProfileTombstone(document.OperationId, actorId!, reason!, document.RecordedAtUtc);
        return true;
    }

    private static AuthorityProfileTombstone MapTombstone(AuthorityProfileTombstoneDocument document)
    {
        _ = TryMapTombstone(document, out var tombstone);
        return tombstone!;
    }

    private static AuthorityProfileRevisionDocument NewRevision(AuthorityProfile profile, string operationId, DateTimeOffset recordedAtUtc)
    {
        _ = AuthorityProfileJson.TrySerialize(profile, out var json, out _);
        _ = AuthorityProfileHash.TryCompute(profile, out var hash, out _);
        return new AuthorityProfileRevisionDocument(profile.Revision.Value, json!, hash!.Value, operationId, recordedAtUtc);
    }

    private static string? ValidateMutation(AuthorityProfileMutation? mutation)
    {
        if (mutation is null || !Enum.IsDefined(mutation.Kind) || !IsOperationIdValid(mutation.OperationId) || mutation.ExpectedRevision is < 0 or int.MaxValue || mutation.ActorId is null || mutation.Reason is null)
        {
            return "The authority-profile operation identity, revision, actor, or reason is invalid.";
        }

        if (mutation.Kind is AuthorityProfileMutationKind.Create or AuthorityProfileMutationKind.Revise)
        {
            if (mutation.Profile is null || mutation.ProfileId is not null || mutation.Status is not null || !AuthorityProfileJson.TrySerialize(mutation.Profile, out _, out _) || mutation.Profile.Revision.Value != checked(mutation.ExpectedRevision + 1))
            {
                return "A create or revise operation requires one complete successor profile at the expected next revision.";
            }

            if (mutation.Kind == AuthorityProfileMutationKind.Create && mutation.ExpectedRevision != 0 || mutation.Kind == AuthorityProfileMutationKind.Revise && mutation.ExpectedRevision == 0)
            {
                return "Create requires revision zero and revise requires an existing positive revision.";
            }
        }
        else if (mutation.Profile is not null || mutation.ProfileId is null || mutation.ExpectedRevision == 0 || mutation.Kind == AuthorityProfileMutationKind.TransitionStatus && (!mutation.Status.HasValue || !Enum.IsDefined(mutation.Status.Value) || mutation.Status == AuthorityProfileStatus.Unknown) || mutation.Kind == AuthorityProfileMutationKind.Tombstone && mutation.Status is not null)
        {
            return "A status transition or tombstone requires only a canonical target and expected current revision.";
        }

        return null;
    }

    private async Task<CapabilityCatalogPathSession> AcquireLockAsync(CancellationToken cancellationToken) => await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.AuthorityProfilesLockPath, false, cancellationToken) ?? throw new IOException("The authority-profile workspace root is unavailable.");

    private static AuthorityProfileStoreDocument EmptyDocument(string identity)
    {
        var empty = new AuthorityProfileStoreDocument(AuthorityProfileStoreDocument.CurrentSchemaVersion, identity, 0, [], [], [], [], string.Empty, string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(AuthorityProfileStoreDocument document) => CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _hashOptions)));

    private static string ComputeRequestHash(AuthorityProfileMutation mutation)
    {
        var profileJson = mutation.Profile is null ? string.Empty : AuthorityProfileJson.TrySerialize(mutation.Profile, out var json, out _) ? json! : string.Empty;
        var content = $"{(int)mutation.Kind}\n{mutation.OperationId}\n{mutation.ExpectedRevision}\n{mutation.ProfileId?.Value ?? mutation.Profile?.ProfileId.Value}\n{(int?)mutation.Status}\n{profileJson}\n{mutation.ActorId.Value}\n{mutation.Reason.Value}";
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value;
    }

    private static bool IsGrantStoreMutationValid(AuthorityGrantStoreMutation? mutation)
    {
        if (mutation is null
            || mutation.ExpectedStoreGeneration < 0
            || !AuthorityGrantContractValidator.Validate(mutation.Operation).IsValid)
        {
            return false;
        }

        var grant = mutation.GrantToAppend;
        var operation = mutation.Operation;
        if (grant is null)
        {
            return ValidateReceiptOnlyAppend(operation) == AuthorityGrantStoreCommitStatus.Committed;
        }

        if (!AuthorityGrantContractValidator.Validate(grant).IsValid)
        {
            return false;
        }

        var result = operation.ResultingGrant;
        return operation.Outcome == AuthorityGrantOperationOutcome.Committed
            && operation.FailureCode == AuthorityGrantOperationFailureCode.None
            && operation.GrantId.Equals(grant.GrantId)
            && operation.ExpectedRevision == grant.Revision.Value - 1L
            && result is not null
            && result.GrantId.Equals(grant.GrantId)
            && result.Revision.Equals(grant.Revision)
            && string.Equals(result.ContentHash, grant.ContentHash, StringComparison.Ordinal)
            && string.Equals(operation.ActorId.Value, grant.ChangedByActorId.Value, StringComparison.Ordinal)
            && string.Equals(operation.Reason.Value, grant.Reason.Value, StringComparison.Ordinal)
            && operation.RecordedAtUtc == grant.RecordedAtUtc
            && (grant.Revision.Value == 1) == (operation.Kind == AuthorityGrantOperationKind.Create);
    }

    private static string CreateWorkspaceIdentity(string physicalIdentity) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("embodysense-authority-profile-workspace-physical-v1\n" + physicalIdentity))).ToLowerInvariant();

    private bool TryGetTrustedUtcNow(out DateTimeOffset recordedAtUtc)
    {
        try
        {
            recordedAtUtc = _timeProvider.GetUtcNow();
            return recordedAtUtc != default && recordedAtUtc.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            recordedAtUtc = default;
            return false;
        }
    }

    private static bool IsOperationIdValid(string? value)
        => value is { Length: > 0 } bounded
            && bounded.Length <= AuthorityProfileStoreLimits.MaximumOperationIdCharacters
            && IsLowerAsciiAlphaNumeric(bounded[0])
            && IsLowerAsciiAlphaNumeric(bounded[^1])
            && bounded.All(character => IsLowerAsciiAlphaNumeric(character) || character is '-' or '_' or '.');

    private static bool IsLowerAsciiAlphaNumeric(char value)
        => value is >= 'a' and <= 'z' or >= '0' and <= '9';
    private static bool IsEvidenceHashValid(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool MatchesCurrent(AuthorityProfileStoreDocument document, CapabilityCatalogTrustState trust) => document.Generation == trust.CurrentGeneration && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);
    private static bool MatchesPrevious(AuthorityProfileStoreDocument document, CapabilityCatalogTrustState trust) => trust.PreviousGeneration == document.Generation && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);
    private static AuthorityProfileMutationResult Result(AuthorityProfileMutationStatus status, string operationId, AuthorityProfileRecord? record, string detail) => new(status, operationId, record, detail);
    private static AuthorityGrantStoreReadResult GrantReadResult(AuthorityGrantStoreReadStatus status, long generation, AuthorityGrantStoreSnapshot? snapshot, AuthorityGrantStoredOperation? operation) => new(status, generation, snapshot, operation);
    private static AuthorityGrantStoreCommitResult GrantCommitResult(AuthorityGrantStoreCommitStatus status, long generation, AuthorityGrantStoredOperation? operation, AuthorityGrantStoreSnapshot? snapshot) => new(status, generation, operation, snapshot);
    private static bool IsAvailabilityFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or FormatException or JsonException or OverflowException;
    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented) => new(JsonSerializerDefaults.Web) { WriteIndented = writeIndented, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, false) } };

    private sealed record LoadResult(AuthorityProfileStoreDocument? Document, bool Recovered, bool Ambiguous = false);
    private sealed record SerializedDocument(string Json, string ContentDigest);
    private sealed record Transition(AuthorityProfileMutationStatus Status, AuthorityProfileDocument? Profile, int? ResultingRevision, string Detail);
}
