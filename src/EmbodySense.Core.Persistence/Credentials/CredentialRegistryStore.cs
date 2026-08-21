using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Leases;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Leases;
using EmbodySense.Core.Common.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Credentials.Models;

namespace EmbodySense.Core.Persistence.Credentials;

/// <summary>Persists schema-1, workspace-bound, value-free credential registry state and immutable evidence.</summary>
/// <remarks>Provider locators are strictly shaped opaque tokens retained only beneath the workspace private boundary. This store never resolves a locator or accepts credential bytes, encrypted envelopes, or key material.</remarks>
public sealed class CredentialRegistryStore : ICredentialRegistryStore
{
    private const int MaximumActiveRuns = 1_024;
    // Credential JSON is bounded by UTF-16 characters. A future terminal record can expand each character to one
    // six-byte JSON escape when embedded in the registry; the remainder covers the operation/envelope fields.
    private const int MaximumReservedTerminalArtifactUtf8Bytes = (6 * CredentialContractLimits.MaxCanonicalJsonCharacters) + (16 * 1024);
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly JsonSerializerOptions _canonicalJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    private static readonly UTF8Encoding _utf8 = new(false, true);
    private readonly WorkspacePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICredentialProviderLocatorVerifier _locatorVerifier;
    private readonly CredentialRegistryQuota _quota;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a registry store rooted at one canonical workspace.</summary>
    public CredentialRegistryStore(WorkspacePaths paths, TimeProvider? timeProvider = null) : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), new RejectingCredentialProviderLocatorVerifier(), timeProvider)
    {
    }

    /// <summary>Creates a registry store over explicit server-owned trust and provider-owned locator verification seams.</summary>
    public CredentialRegistryStore(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, ICredentialProviderLocatorVerifier locatorVerifier, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, CredentialRegistryQuota? quota = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _trustProvider = trustProvider ?? throw new ArgumentNullException(nameof(trustProvider));
        if (_trustProvider.MaximumAuthenticationTagUtf8Bytes < 1 || _trustProvider.MaximumAuthenticationTagUtf8Bytes > CredentialRegistryLimits.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(trustProvider), "The trust provider must declare a positive bounded authentication-tag size.");
        }

        _locatorVerifier = locatorVerifier ?? throw new ArgumentNullException(nameof(locatorVerifier));
        _quota = ValidateQuota(quota);
        _pathGuard = new CapabilityCatalogPathGuard(paths.WorkspacePath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Rejects raw actor claims because only the private lifecycle projection owns process identity.</summary>
    public ValueTask<CredentialActorAuthentication> AuthenticateActorAsync(string actorId, CancellationToken cancellationToken) => ValueTask.FromResult(CredentialActorAuthentication.Unauthenticated);

    /// <inheritdoc />
    public async ValueTask<CredentialReferenceLookupResult> GetAsync(CredentialReferenceId referenceId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(referenceId);
        var read = await ReadAsync(cancellationToken);
        if (!read.Succeeded)
        {
            return CredentialReferenceLookupResult.Failed(read.Failure!);
        }

        var entry = read.Entries.SingleOrDefault(item => item.Reference.Id.Equals(referenceId));
        return entry is null ? CredentialReferenceLookupResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.NotFound)) : CredentialReferenceLookupResult.Found(entry.Reference);
    }

    /// <inheritdoc />
    public async Task<CredentialRegistryReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var state = await LoadAsync(session, cancellationToken);
            return state is null ? FailedRead(CredentialFailureCode.Unavailable) : ToReadResult(state);
        }
        catch (OperationCanceledException)
        {
            return FailedRead(CredentialFailureCode.Unavailable);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return FailedRead(CredentialFailureCode.Unavailable);
        }
    }

    /// <summary>Rejects raw registry mutations because only the private lifecycle projection owns mutation authority.</summary>
    /// <remarks>Lifecycle services use a private assembly boundary. Public callers can read value-free state and append governed use evidence, but cannot mutate registry state or mint lifecycle evidence.</remarks>
    public Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default) => MutateAsync(mutation, lifecycleAuthorized: false, cancellationToken);

    internal Task<CredentialRegistryMutationResult> MutateLifecycleAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default) => MutateAsync(mutation, lifecycleAuthorized: true, cancellationToken);

    private async Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, bool lifecycleAuthorized, CancellationToken cancellationToken)
    {
        if (mutation is null)
        {
            return Mutation(CredentialRegistryMutationStatus.Invalid, null, null, null, CredentialFailureCode.InvalidRequest);
        }
        if (mutation.AffectedActiveRuns is not null)
        {
            mutation = mutation with { AffectedActiveRuns = Array.AsReadOnly(mutation.AffectedActiveRuns.ToArray()) };
        }
        if (!lifecycleAuthorized)
        {
            return Mutation(CredentialRegistryMutationStatus.Invalid, mutation.OperationId, null, null, CredentialFailureCode.Unauthorized);
        }
        if (!ValidateMutation(mutation, out var requestHash))
        {
            return Mutation(CredentialRegistryMutationStatus.Invalid, mutation?.OperationId, null, null, CredentialFailureCode.InvalidRequest);
        }

        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var current = await LoadAsync(session, cancellationToken);
            if (current is null || current.Recovered)
            {
                return Mutation(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, null, null, CredentialFailureCode.Unavailable);
            }

            var prior = current.Public.Operations.SingleOrDefault(item => string.Equals(item.OperationId, mutation.OperationId.Value, StringComparison.Ordinal));
            if (prior is not null)
            {
                if (!FixedTimeEquals(prior.RequestHash, requestHash!))
                {
                    return Mutation(CredentialRegistryMutationStatus.Conflict, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Conflict);
                }

                var replayEntry = prior.ResultEntry is not null && TryMapEntry(prior.ResultEntry, out var mappedReceipt) ? mappedReceipt : null;
                return Mutation(CredentialRegistryMutationStatus.Replayed, mutation.OperationId, prior.Revision, replayEntry, null);
            }

            if (!ValidateLifecyclePhaseAgainstState(current, mutation))
            {
                return Mutation(CredentialRegistryMutationStatus.Conflict, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Conflict);
            }

            if (!await VerifyLocatorAsync(current, mutation, cancellationToken))
            {
                return Mutation(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Unavailable);
            }

            if (mutation.ExpectedRegistryRevision != current.Public.Revision)
            {
                return Mutation(CredentialRegistryMutationStatus.Conflict, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Conflict);
            }

            var newRegistrationExceedsEntryQuota = current.Public.Entries.Count >= _quota.MaximumEntries && (mutation.Kind == CredentialRegistryMutationKind.Register || mutation.Kind == CredentialRegistryMutationKind.BeginCreate && current.Public.Entries.All(item => !Matches(item, mutation.ReferenceId)));
            if (current.Public.Operations.Count + current.Public.EvidenceReservations!.Count >= _quota.MaximumOperations || newRegistrationExceedsEntryQuota || mutation.Kind == CredentialRegistryMutationKind.Tombstone && current.Public.Tombstones.Count >= _quota.MaximumTombstones)
            {
                return Mutation(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.LimitExceeded);
            }

            var candidate = Apply(current, mutation, requestHash!);
            if (candidate is null)
            {
                return Mutation(CredentialRegistryMutationStatus.Conflict, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Conflict);
            }

            await CommitAsync(session, current, candidate, cancellationToken);
            return Mutation(CredentialRegistryMutationStatus.Applied, mutation.OperationId, candidate.Public.Revision, FindEntry(candidate.Public, mutation.ReferenceId), null);
        }
        catch (OperationCanceledException)
        {
            return Mutation(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, null, null, CredentialFailureCode.Unavailable);
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return Mutation(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, null, null, CredentialFailureCode.Unavailable);
        }
    }

    /// <summary>Rejects raw audit acknowledgement because only the lifecycle audit drain owns delivery authority.</summary>
    public Task<bool> AcknowledgeAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    internal async Task<bool> AcknowledgeLifecycleAuditAsync(CredentialContractId auditOperationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditOperationId);
        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var current = await LoadAsync(session, cancellationToken);
            if (current is null || current.Recovered)
            {
                return false;
            }
            var auditOperation = current.Public.Operations.SingleOrDefault(item => string.Equals(item.OperationId, auditOperationId.Value, StringComparison.Ordinal));
            if (auditOperation?.AuditOutbox is null)
            {
                return false;
            }
            if (current.Public.AuditDeliveries!.Any(item => string.Equals(item.TerminalOperationId, auditOperationId.Value, StringComparison.Ordinal)))
            {
                return true;
            }

            var delivery = new CredentialRegistryAuditDeliveryDocument(auditOperationId.Value, _timeProvider.GetUtcNow());
            var publicCandidate = current.Public with { Generation = checked(current.Public.Generation + 1), AuditDeliveries = [.. current.Public.AuditDeliveries!, delivery] };
            var candidate = Complete(current.WorkspaceIdentity, publicCandidate, current.Private);
            await CommitAsync(session, current, candidate, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<CredentialEvidenceWriteResult> ReserveAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken)
    {
        if (CredentialLeaseContract.Validate(intent) is not null)
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest));
        }

        var evidenceId = CredentialLeaseContract.ComputeEvidenceId(intent.CredentialUseOperationId, intent.CredentialUseGeneration);
        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var current = await LoadAsync(session, cancellationToken);
            if (current is null || current.Recovered)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
            }
            var reservations = current.Public.EvidenceReservations ?? [];

            var existingEvidence = current.Public.Evidence.SingleOrDefault(item => HasEvidenceId(item, evidenceId));
            if (existingEvidence is not null)
            {
                return CredentialContractJson.TryDeserializeEvidence(existingEvidence.EvidenceJson, out var mapped, out _)
                    && mapped!.Lease is not null
                    && string.Equals(mapped.Lease.Intent.ContentHash, intent.ContentHash, StringComparison.Ordinal)
                    ? CredentialEvidenceWriteResult.Success()
                    : CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            var existingReservation = reservations.SingleOrDefault(item => string.Equals(item.EvidenceId, evidenceId.Value, StringComparison.Ordinal));
            if (existingReservation is not null)
            {
                return ReservationMatches(existingReservation, intent)
                    ? CredentialEvidenceWriteResult.Success()
                    : CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            if (!TryGetUtcNow(out var trustedNowUtc))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
            }
            var registryMatch = CredentialLeaseRegistryMatcher.Match(intent, ToReadResult(current), trustedNowUtc);
            if (!registryMatch.Succeeded)
            {
                return CredentialEvidenceWriteResult.Failed(registryMatch.Failure ?? CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            if (current.Public.Evidence.Count + reservations.Count >= _quota.MaximumEvidence
                || current.Public.Operations.Count + reservations.Count >= _quota.MaximumOperations
                || current.Public.Operations.Any(item => string.Equals(item.OperationId, evidenceId.Value, StringComparison.Ordinal)))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.LimitExceeded));
            }

            var reservation = new CredentialRegistryEvidenceReservationDocument(
                evidenceId.Value,
                intent.CredentialUseOperationId,
                intent.CredentialUseGeneration,
                intent.ContentHash,
                intent.Registry.ReferenceId,
                intent.Registry.BindingHash);
            var publicCandidate = current.Public with
            {
                Generation = checked(current.Public.Generation + 1),
                EvidenceReservations = [.. reservations, reservation],
            };
            var candidate = Complete(current.WorkspaceIdentity, publicCandidate, current.Private);
            if (!FitsArtifactSize(candidate))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.LimitExceeded));
            }
            await CommitAsync(session, current, candidate, cancellationToken);
            return CredentialEvidenceWriteResult.Success();
        }
        catch (OperationCanceledException)
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
    }

    /// <inheritdoc />
    public async ValueTask<CredentialEvidenceWriteResult> AppendAsync(CredentialUseEvidence evidence, CancellationToken cancellationToken)
    {
        if (!CredentialContractJson.TrySerialize(evidence, out var evidenceJson, out _))
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.InvalidRequest));
        }

        try
        {
            await using var session = await AcquireAsync(cancellationToken);
            var current = await LoadAsync(session, cancellationToken);
            if (current is null || current.Recovered)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
            }
            var reservations = current.Public.EvidenceReservations ?? [];

            var existing = current.Public.Evidence.SingleOrDefault(item => HasEvidenceId(item, evidence!.EvidenceId));
            if (existing is not null)
            {
                return string.Equals(existing.EvidenceJson, evidenceJson, StringComparison.Ordinal) ? CredentialEvidenceWriteResult.Success() : CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            var reservation = reservations.SingleOrDefault(item => string.Equals(item.EvidenceId, evidence!.EvidenceId.Value, StringComparison.Ordinal));
            var boundaryCrossed = evidence.Lease?.TerminalPhase is CredentialLeasePhase.Redeemed or CredentialLeasePhase.RedemptionFailed or CredentialLeasePhase.RedemptionAmbiguous;
            var reservationBacked = reservation is not null && evidence.Lease is not null && ReservationMatches(reservation, evidence.Lease.Intent);
            if (boundaryCrossed && !reservationBacked)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }
            if (reservation is not null && !reservationBacked)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            var reference = FindEntry(current.Public, evidence.ReferenceId);
            var requiresCurrentBinding = !boundaryCrossed && !reservationBacked;
            if (requiresCurrentBinding && reference is null)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.NotFound));
            }

            if (requiresCurrentBinding && reference!.Health == CredentialProviderHealthStatus.NeedsRepair)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            if (requiresCurrentBinding && !reference!.BindingHash.FixedTimeEquals(evidence.BindingHash))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            if (requiresCurrentBinding && !CredentialScopeRules.IsNarrowerThanOrEqual(evidence.UsedScope, reference!.Binding.Scope))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized));
            }

            if (reservation is null && (current.Public.Evidence.Count + reservations.Count >= _quota.MaximumEvidence || current.Public.Operations.Count + reservations.Count >= _quota.MaximumOperations)
                || current.Public.Operations.Any(item => string.Equals(item.OperationId, evidence!.EvidenceId.Value, StringComparison.Ordinal)))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.LimitExceeded));
            }

            var revision = checked(current.Public.Revision + 1);
            var operation = new CredentialRegistryOperationDocument(evidence!.EvidenceId.Value, Hash("evidence\n" + evidenceJson), -1, revision, evidence.ReferenceId.Value, null);
            var publicCandidate = current.Public with
            {
                Generation = checked(current.Public.Generation + 1),
                Revision = revision,
                Evidence = [.. current.Public.Evidence, new CredentialRegistryEvidenceDocument(evidenceJson!)],
                Operations = [.. current.Public.Operations, operation],
                EvidenceReservations = reservation is null ? reservations : reservations.Where(item => !string.Equals(item.EvidenceId, reservation.EvidenceId, StringComparison.Ordinal)).ToArray(),
            };
            var candidate = Complete(current.WorkspaceIdentity, publicCandidate, current.Private with { Revision = revision });
            await CommitAsync(session, current, candidate, cancellationToken);
            return CredentialEvidenceWriteResult.Success();
        }
        catch (OperationCanceledException)
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
        catch (Exception exception) when (IsStorageFailure(exception))
        {
            return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unavailable));
        }
    }

    private async Task<State?> LoadAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var identity = WorkspaceIdentity(session.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
        var primary = await TryReadPairAsync(session, _paths.CredentialRegistryDocumentPath, _paths.CredentialRegistryPrivateDocumentPath, identity, cancellationToken);
        if (primary is not null && trust is not null && MatchesCurrent(primary.Public, trust))
        {
            return primary;
        }

        if (trust is not null && IsInitialEmptyTrust(identity, trust))
        {
            return Empty(identity);
        }

        var proof = await TryReadPairAsync(session, _paths.CredentialRegistryProofPath, _paths.CredentialRegistryPrivateProofPath, identity, cancellationToken);
        if (proof is not null && trust is not null && (MatchesCurrent(proof.Public, trust) || MatchesPrevious(proof.Public, trust)))
        {
            return proof with { Recovered = true };
        }

        var anyArtifact = session.FileExists(_paths.CredentialRegistryDocumentPath) || session.FileExists(_paths.CredentialRegistryPrivateDocumentPath) || session.FileExists(_paths.CredentialRegistryProofPath) || session.FileExists(_paths.CredentialRegistryPrivateProofPath);
        return anyArtifact || trust is not null ? null : Empty(identity);
    }

    private async Task<State?> TryReadPairAsync(CapabilityCatalogPathSession session, string publicPath, string privatePath, string identity, CancellationToken cancellationToken)
    {
        if (!session.FileExists(publicPath) || !session.FileExists(privatePath))
        {
            return null;
        }

        try
        {
            var publicDocument = JsonSerializer.Deserialize<CredentialRegistryDocument>(_utf8.GetString(await session.ReadAllBytesAsync(publicPath, _quota.MaximumArtifactUtf8Bytes, cancellationToken)), _json);
            var privateDocument = JsonSerializer.Deserialize<CredentialRegistryPrivateDocument>(_utf8.GetString(await session.ReadAllBytesAsync(privatePath, _quota.MaximumArtifactUtf8Bytes, cancellationToken)), _json);
            return publicDocument is not null && privateDocument is not null && ValidatePair(publicDocument, privateDocument, identity) && await _trustProvider.VerifyArtifactAsync(identity, publicDocument.Generation, publicDocument.ContentDigest, publicDocument.AuthenticationTag, cancellationToken) ? new State(identity, publicDocument, privateDocument, false) : null;
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

    private State? Apply(State current, CredentialRegistryMutation mutation, string requestHash)
    {
        var revision = checked(current.Public.Revision + 1);
        var entries = current.Public.Entries.ToList();
        var tombstones = current.Public.Tombstones.ToList();
        var locators = current.Private.Locators.ToList();
        var existing = entries.SingleOrDefault(item => Matches(item, mutation.ReferenceId));
        CredentialRegistryEntryDocument? operationResultEntry = null;
        if (mutation.Kind == CredentialRegistryMutationKind.BeginCreate)
        {
            if (existing is not null || tombstones.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal)) || current.Public.Operations.Any(item => item.Kind == (int)CredentialRegistryMutationKind.BeginCreate && string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal)))
            {
                return null;
            }

            CredentialContractJson.TrySerialize(mutation.Reference, out var referenceJson, out _);
            CredentialContractJson.TrySerialize(mutation.Binding, out var bindingJson, out _);
            CredentialContractJson.TryHash(mutation.Binding, out var bindingHash, out _);
            operationResultEntry = new CredentialRegistryEntryDocument(referenceJson!, bindingJson!, bindingHash!.Value, mutation.ConsentReference!.Value, (int)mutation.Health!.Value, revision, mutation.OperationId.Value, false);
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Register)
        {
            var unresolvedCreateIntent = current.Public.Operations.Any(item => item.Kind == (int)CredentialRegistryMutationKind.BeginCreate && string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
            if (existing is not null || tombstones.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal)) || unresolvedCreateIntent && mutation.LifecyclePhase != CredentialLifecycleMutationPhase.LocatorPrepared)
            {
                return null;
            }

            CredentialContractJson.TrySerialize(mutation.Reference, out var referenceJson, out _);
            CredentialContractJson.TrySerialize(mutation.Binding, out var bindingJson, out _);
            CredentialContractJson.TryHash(mutation.Binding, out var bindingHash, out _);
            entries.Add(new CredentialRegistryEntryDocument(referenceJson!, bindingJson!, bindingHash!.Value, mutation.ConsentReference!.Value, (int)mutation.Health!.Value, revision, mutation.OperationId.Value, false));
            locators.Add(new CredentialRegistryLocatorDocument(mutation.ReferenceId.Value, mutation.ProviderLocator!.Value, false));
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.SetHealth)
        {
            var existingHealth = existing is null ? (CredentialProviderHealthStatus?)null : (CredentialProviderHealthStatus)existing.Health;
            var resolvesIntent = mutation.LifecyclePhase is CredentialLifecycleMutationPhase.Complete or CredentialLifecycleMutationPhase.Rollback or CredentialLifecycleMutationPhase.Uncertain;
            if (existing is null || IsRestrictive(existing) && mutation.Health is not (CredentialProviderHealthStatus.Revoked or CredentialProviderHealthStatus.Disabled or CredentialProviderHealthStatus.Expired) || existingHealth == CredentialProviderHealthStatus.NeedsRepair && !resolvesIntent)
            {
                return null;
            }

            entries[entries.IndexOf(existing)] = existing with { Health = (int)mutation.Health!.Value, Revision = revision, LastOperationId = mutation.OperationId.Value };
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Tombstone)
        {
            var resolvesIntent = mutation.LifecyclePhase is CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain or CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.RepairUncertain;
            if (existing is null || (CredentialProviderHealthStatus)existing.Health == CredentialProviderHealthStatus.NeedsRepair && !resolvesIntent || !CredentialContractJson.TryDeserializeReference(existing.ReferenceJson, out var reference, out _) || !CredentialContractJson.TryHash(reference, out var referenceHash, out _))
            {
                return null;
            }

            var repairRequired = mutation.LifecyclePhase is CredentialLifecycleMutationPhase.TombstoneUncertain or CredentialLifecycleMutationPhase.RepairUncertain;
            entries.Remove(existing);
            if (repairRequired)
            {
                var locatorIndex = locators.FindIndex(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
                if (locatorIndex < 0)
                {
                    return null;
                }
                locators[locatorIndex] = locators[locatorIndex] with { RepairRequired = true };
            }
            else
            {
                locators.RemoveAll(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
            }
            tombstones.Add(new CredentialRegistryTombstoneDocument(mutation.ReferenceId.Value, revision, mutation.OperationId.Value, _timeProvider.GetUtcNow(), referenceHash!.Value, repairRequired, repairRequired ? existing.BindingJson : null, repairRequired ? reference!.ProviderId.Value : null));
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Bind)
        {
            if (existing is null || (CredentialProviderHealthStatus)existing.Health == CredentialProviderHealthStatus.NeedsRepair || !CredentialContractJson.TryDeserializeReference(existing.ReferenceJson, out var existingReference, out _) || !string.Equals(existingReference!.ProviderId.Value, mutation.Binding!.Implementation.ProviderId.Value, StringComparison.Ordinal) || !CredentialContractJson.TrySerialize(mutation.Binding, out var bindingJson, out _) || !CredentialContractJson.TryHash(mutation.Binding, out var bindingHash, out _))
            {
                return null;
            }

            entries[entries.IndexOf(existing)] = existing with { BindingJson = bindingJson!, BindingHash = bindingHash!.Value, Revision = revision, LastOperationId = mutation.OperationId.Value };
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Consent)
        {
            if (existing is null || (CredentialProviderHealthStatus)existing.Health == CredentialProviderHealthStatus.NeedsRepair)
            {
                return null;
            }

            entries[entries.IndexOf(existing)] = existing with { ConsentReference = mutation.ConsentReference!.Value, ConsentGranted = mutation.ConsentGranted!.Value, Revision = revision, LastOperationId = mutation.OperationId.Value };
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.UpdatePosture)
        {
            if (existing is null || (CredentialProviderHealthStatus)existing.Health == CredentialProviderHealthStatus.NeedsRepair || !CredentialContractJson.TryDeserializeReference(existing.ReferenceJson, out var existingReference, out _) || !CredentialContractJson.TrySerialize(mutation.Reference, out var referenceJson, out _) || !CredentialContractJson.TrySerialize(mutation.Reference! with { Status = existingReference!.Status, UpdatedAtUtc = existingReference.UpdatedAtUtc }, out var normalizedJson, out _) || !string.Equals(normalizedJson, existing.ReferenceJson, StringComparison.Ordinal))
            {
                return null;
            }

            entries[entries.IndexOf(existing)] = existing with { ReferenceJson = referenceJson!, Health = (int)mutation.Health!.Value, Revision = revision, LastOperationId = mutation.OperationId.Value };
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.BeginRepair)
        {
            var repair = tombstones.SingleOrDefault(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
            var preparedCreate = IsPreparedCreateState(current.Public, existing, locators, mutation.ReferenceId);
            var tombstoneRepair = existing is null && repair is { NeedsRepair: true } && locators.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && item.RepairRequired);
            if (!preparedCreate && !tombstoneRepair || HasUnresolvedRepairIntent(current.Public, mutation.ReferenceId))
            {
                return null;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.CompleteRepair)
        {
            var repair = tombstones.SingleOrDefault(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && item.NeedsRepair);
            if (existing is not null || repair is null || !locators.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && item.RepairRequired))
            {
                return null;
            }
            locators.RemoveAll(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.RecordRepairUncertain)
        {
            var repair = tombstones.SingleOrDefault(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && item.NeedsRepair);
            if (existing is not null || repair is null || !locators.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && item.RepairRequired))
            {
                return null;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.ReconcileRepair)
        {
            if (existing is not null)
            {
                _ = TryMapEntry(existing, out var mapped);
                _ = CredentialContractJson.TryHash(mapped!.Reference, out var referenceHash, out _);
                var locatorIndex = locators.FindIndex(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && !item.RepairRequired);
                entries.Remove(existing);
                locators[locatorIndex] = locators[locatorIndex] with { RepairRequired = true };
                tombstones.Add(new CredentialRegistryTombstoneDocument(mutation.ReferenceId.Value, revision, mutation.OperationId.Value, _timeProvider.GetUtcNow(), referenceHash!.Value, true, existing.BindingJson, mapped.Reference.ProviderId.Value));
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.RecordLocatorUncertain)
        {
            if (tombstones.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal)) || existing is not null && !IsPreparedCreateState(current.Public, existing, locators, mutation.ReferenceId))
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        var resultEntry = operationResultEntry ?? (mutation.Kind is CredentialRegistryMutationKind.Tombstone or CredentialRegistryMutationKind.BeginRepair or CredentialRegistryMutationKind.CompleteRepair or CredentialRegistryMutationKind.RecordRepairUncertain or CredentialRegistryMutationKind.RecordLocatorUncertain or CredentialRegistryMutationKind.ReconcileRepair ? null : entries.Single(item => Matches(item, mutation.ReferenceId)));
        var auditOutbox = mutation.LifecycleAudit is null ? null : new CredentialRegistryAuditOutboxDocument(_timeProvider.GetUtcNow(), revision, mutation.LifecycleAudit.Action, mutation.LifecycleAudit.Outcome, mutation.LifecycleAudit.Detail);
        var operation = new CredentialRegistryOperationDocument(mutation.OperationId.Value, requestHash, (int)mutation.Kind, revision, mutation.ReferenceId.Value, resultEntry, mutation.LifecycleOperation, mutation.ActorId, mutation.PreviewHash, mutation.LifecycleRequestHash, (int?)mutation.LifecyclePhase, mutation.LifecycleIntentOperationId?.Value, mutation.AffectedActiveRuns?.ToArray(), mutation.WorkspaceId, auditOutbox);
        var publicCandidate = current.Public with { Generation = checked(current.Public.Generation + 1), Revision = revision, Entries = entries.OrderBy(GetReferenceId, StringComparer.Ordinal).ToArray(), Tombstones = tombstones.OrderBy(item => item.ReferenceId, StringComparer.Ordinal).ToArray(), Operations = [.. current.Public.Operations, operation] };
        var privateCandidate = current.Private with { Revision = revision, Locators = locators.OrderBy(item => item.ReferenceId, StringComparer.Ordinal).ToArray() };
        return Complete(current.WorkspaceIdentity, publicCandidate, privateCandidate);
    }

    private async Task CommitAsync(CapabilityCatalogPathSession session, State current, State candidate, CancellationToken cancellationToken)
    {
        PreflightArtifactSize(current);
        PreflightArtifactSize(candidate);
        var currentDigest = ComputeDocumentDigest(current.Public);
        var trust = await _trustProvider.ReadAsync(current.WorkspaceIdentity, cancellationToken) ?? await _trustProvider.InitializeAsync(current.WorkspaceIdentity, current.Public.Generation, currentDigest, cancellationToken);
        if (!MatchesCurrent(current.Public with { ContentDigest = currentDigest }, trust))
        {
            throw new IOException("The server-owned credential registry trust anchor no longer matches the mutation base.");
        }

        var currentSerialized = await SerializeAsync(current, cancellationToken);
        var candidateSerialized = await SerializeAsync(candidate, cancellationToken);

        await WritePairAsync(session, _paths.CredentialRegistryProofPath, _paths.CredentialRegistryPrivateProofPath, currentSerialized, cancellationToken);
        await WritePairAsync(session, _paths.CredentialRegistryDocumentPath, _paths.CredentialRegistryPrivateDocumentPath, candidateSerialized, cancellationToken);
        _ = await _trustProvider.AdvanceAsync(current.WorkspaceIdentity, trust.CurrentGeneration, trust.CurrentContentDigest, candidate.Public.Generation, candidateSerialized.ContentDigest, cancellationToken);
    }

    private void PreflightArtifactSize(State state)
    {
        if (!FitsArtifactSize(state))
        {
            throw new IOException("The bounded credential-registry artifact limit would be exceeded.");
        }
    }

    private bool FitsArtifactSize(State state)
    {
        var authenticationTagPlaceholder = new string('\u0001', _trustProvider.MaximumAuthenticationTagUtf8Bytes);
        var digest = ComputeDocumentDigest(state.Public);
        var publicJson = JsonSerializer.Serialize(state.Public with { ContentDigest = digest, AuthenticationTag = authenticationTagPlaceholder }, _json) + Environment.NewLine;
        var privateJson = JsonSerializer.Serialize(state.Private, _json) + Environment.NewLine;
        var reservations = state.Public.EvidenceReservations?.Count ?? 0;
        var reservedTerminalBytes = checked((long)reservations * MaximumReservedTerminalArtifactUtf8Bytes);
        return _utf8.GetByteCount(publicJson) <= (long)_quota.MaximumArtifactUtf8Bytes - reservedTerminalBytes
            && _utf8.GetByteCount(privateJson) <= _quota.MaximumArtifactUtf8Bytes;
    }

    private async Task WritePairAsync(CapabilityCatalogPathSession session, string publicPath, string privatePath, SerializedState state, CancellationToken cancellationToken)
    {
        await session.WriteTextAtomicallyAsync(publicPath, state.PublicJson, cancellationToken);
        await session.WriteTextAtomicallyAsync(privatePath, state.PrivateJson, cancellationToken);
    }

    private async Task<CapabilityCatalogPathSession> AcquireAsync(CancellationToken cancellationToken)
    {
        return await _pathGuard.TryAcquireExclusiveSessionAsync(_paths.CredentialRegistryLockPath, createRoot: false, cancellationToken) ?? throw new IOException("The credential registry workspace root is unavailable.");
    }

    private async Task<SerializedState> SerializeAsync(State state, CancellationToken cancellationToken)
    {
        var digest = ComputeDocumentDigest(state.Public);
        var tag = ValidateAuthenticationTag(await _trustProvider.AuthenticateArtifactAsync(state.WorkspaceIdentity, state.Public.Generation, digest, cancellationToken));
        var publicJson = JsonSerializer.Serialize(state.Public with { ContentDigest = digest, AuthenticationTag = tag }, _json) + Environment.NewLine;
        var privateJson = JsonSerializer.Serialize(state.Private, _json) + Environment.NewLine;
        if (_utf8.GetByteCount(publicJson) > _quota.MaximumArtifactUtf8Bytes || _utf8.GetByteCount(privateJson) > _quota.MaximumArtifactUtf8Bytes)
        {
            throw new IOException("The bounded credential-registry artifact limit would be exceeded.");
        }

        return new SerializedState(publicJson, privateJson, digest);
    }

    private string ValidateAuthenticationTag(string? authenticationTag)
    {
        if (string.IsNullOrEmpty(authenticationTag) || _utf8.GetByteCount(authenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes)
        {
            throw new IOException("The trust provider returned an authentication tag outside its declared bound.");
        }

        return authenticationTag;
    }

    private static string ComputeDocumentDigest(CredentialRegistryDocument document) => Hash(JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _canonicalJson));

    private async ValueTask<bool> VerifyLocatorAsync(State current, CredentialRegistryMutation mutation, CancellationToken cancellationToken)
    {
        if (mutation.Kind is CredentialRegistryMutationKind.Tombstone or CredentialRegistryMutationKind.BeginRepair or CredentialRegistryMutationKind.CompleteRepair or CredentialRegistryMutationKind.RecordRepairUncertain or CredentialRegistryMutationKind.BeginCreate or CredentialRegistryMutationKind.RecordLocatorUncertain or CredentialRegistryMutationKind.ReconcileRepair || mutation.LifecyclePhase == CredentialLifecycleMutationPhase.Uncertain || mutation.Kind == CredentialRegistryMutationKind.UpdatePosture && mutation.Health is CredentialProviderHealthStatus.NeedsRepair or CredentialProviderHealthStatus.Revoked or CredentialProviderHealthStatus.Disabled or CredentialProviderHealthStatus.Expired)
        {
            return true;
        }

        CredentialProviderLocator? locator = mutation.ProviderLocator;
        if (locator is null)
        {
            var stored = current.Private.Locators.SingleOrDefault(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
            if (stored is null || !CredentialProviderLocator.TryParse(stored.Locator, out locator))
            {
                return false;
            }
        }

        var provider = mutation.Reference?.ProviderId ?? FindEntry(current.Public, mutation.ReferenceId)?.Reference.ProviderId;
        return provider is not null && await _locatorVerifier.VerifyAsync(current.WorkspaceIdentity, mutation.ReferenceId, provider, locator!, cancellationToken);
    }

    private static bool ValidateMutation(CredentialRegistryMutation? mutation, out string? requestHash)
    {
        requestHash = null;
        if (mutation is null || !Enum.IsDefined(mutation.Kind) || mutation.ExpectedRegistryRevision < 0 || mutation.OperationId is null || mutation.ReferenceId is null || mutation.LifecycleOperation is < 1 or > 13 || mutation.ActorId is not null && !IsSafe(mutation.ActorId, 128) || mutation.WorkspaceId is not null && !IsSafe(mutation.WorkspaceId, 256) || mutation.PreviewHash is not null && !CredentialContractHash.TryParse(mutation.PreviewHash, out _, out _) || mutation.LifecycleRequestHash is not null && !CredentialContractHash.TryParse(mutation.LifecycleRequestHash, out _, out _) || mutation.LifecyclePhase is not null && !Enum.IsDefined(mutation.LifecyclePhase.Value) || mutation.LifecyclePhase is null != (mutation.LifecycleIntentOperationId is null) || !ValidateActiveRuns(mutation.AffectedActiveRuns))
        {
            return false;
        }

        if (mutation.Kind is CredentialRegistryMutationKind.Register or CredentialRegistryMutationKind.BeginCreate)
        {
            var locatorShapeIsValid = mutation.Kind == CredentialRegistryMutationKind.Register ? mutation.ProviderLocator is not null : mutation.ProviderLocator is null;
            if (mutation.Reference is null || mutation.Binding is null || mutation.ConsentReference is null || mutation.Health is null || !locatorShapeIsValid || mutation.ConsentGranted is true || !Enum.IsDefined(mutation.Health.Value) || !mutation.Reference.Id.Equals(mutation.ReferenceId) || !mutation.Binding.ReferenceId.Equals(mutation.ReferenceId) || !string.Equals(mutation.Reference.ProviderId.Value, mutation.Binding.Implementation.ProviderId.Value, StringComparison.Ordinal) || !CredentialContractJson.TrySerialize(mutation.Reference, out _, out _) || !CredentialContractJson.TrySerialize(mutation.Binding, out _, out _))
            {
                return false;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.SetHealth)
        {
            if (mutation.Health is null || mutation.Reference is not null || mutation.Binding is not null || mutation.ConsentReference is not null || mutation.ProviderLocator is not null || mutation.ConsentGranted is not null || !Enum.IsDefined(mutation.Health.Value))
            {
                return false;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Tombstone)
        {
            if (mutation.Reference is not null || mutation.Binding is not null || mutation.ConsentReference is not null || mutation.Health is not null || mutation.ProviderLocator is not null || mutation.ConsentGranted is not null)
            {
                return false;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Bind)
        {
            if (mutation.Reference is not null || mutation.Binding is null || mutation.ConsentReference is not null || mutation.Health is not null || mutation.ProviderLocator is not null || mutation.ConsentGranted is not null || !mutation.Binding.ReferenceId.Equals(mutation.ReferenceId) || !CredentialContractJson.TrySerialize(mutation.Binding, out _, out _))
            {
                return false;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Consent)
        {
            if (mutation.Reference is not null || mutation.Binding is not null || mutation.ConsentReference is null || mutation.Health is not null || mutation.ProviderLocator is not null || mutation.ConsentGranted is null)
            {
                return false;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.UpdatePosture)
        {
            if (mutation.Reference is null || mutation.Binding is not null || mutation.ConsentReference is not null || mutation.Health is null || mutation.ProviderLocator is not null || mutation.ConsentGranted is not null || !mutation.Reference.Id.Equals(mutation.ReferenceId) || !Enum.IsDefined(mutation.Health.Value) || !CredentialContractJson.TrySerialize(mutation.Reference, out _, out _))
            {
                return false;
            }
        }
        else if (mutation.Kind is CredentialRegistryMutationKind.BeginRepair or CredentialRegistryMutationKind.CompleteRepair or CredentialRegistryMutationKind.RecordRepairUncertain or CredentialRegistryMutationKind.RecordLocatorUncertain or CredentialRegistryMutationKind.ReconcileRepair)
        {
            if (mutation.Reference is not null || mutation.Binding is not null || mutation.ConsentReference is not null || mutation.Health is not null || mutation.ProviderLocator is not null || mutation.ConsentGranted is not null)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var restrictive = mutation.Kind == CredentialRegistryMutationKind.UpdatePosture && (mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Expire || mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Revoke || mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Disable);
        var audited = mutation.LifecyclePhase == CredentialLifecycleMutationPhase.Intent || IsTerminalPhase(mutation.LifecyclePhase);
        if (restrictive != (mutation.AffectedActiveRuns is not null) || audited != (mutation.LifecycleAudit is not null) || mutation.LifecycleAudit is not null && (!string.Equals(mutation.LifecycleAudit.Action, ExpectedAuditAction(mutation.LifecyclePhase), StringComparison.Ordinal) || !string.Equals(mutation.LifecycleAudit.Outcome, ExpectedAuditOutcome(mutation.LifecyclePhase, mutation.LifecycleOperation, mutation.Health), StringComparison.Ordinal) || !IsSafe(mutation.LifecycleAudit.Detail, 512)) || !ValidateLifecyclePhaseShape(mutation))
        {
            return false;
        }

        requestHash = Hash(JsonSerializer.Serialize(mutation, _canonicalJson));
        return true;
    }

    private static bool ValidateActiveRuns(IReadOnlyList<string>? runs)
    {
        return runs is null || runs.Count <= MaximumActiveRuns && runs.All(run => run.Length is > 0 and <= 256 && run.All(character => character >= (char)0x20 && character != (char)0x7f)) && runs.Distinct(StringComparer.Ordinal).Count() == runs.Count && runs.SequenceEqual(runs.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool ValidateLifecyclePhaseShape(CredentialRegistryMutation mutation)
    {
        if (mutation.LifecyclePhase is null)
        {
            return true;
        }
        if (mutation.LifecycleOperation is null || mutation.ActorId is null || mutation.WorkspaceId is null || mutation.LifecycleRequestHash is null || mutation.LifecycleIntentOperationId is null)
        {
            return false;
        }
        return mutation.LifecyclePhase switch
        {
            CredentialLifecycleMutationPhase.Intent => mutation.OperationId.Equals(mutation.LifecycleIntentOperationId) && (mutation.Kind == CredentialRegistryMutationKind.BeginCreate && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import && mutation.Health == CredentialProviderHealthStatus.NeedsRepair || mutation.Kind == CredentialRegistryMutationKind.UpdatePosture && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace or (int)CredentialLifecycleOperationKind.Delete && mutation.Health == CredentialProviderHealthStatus.NeedsRepair || mutation.Kind == CredentialRegistryMutationKind.BeginRepair && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair),
            CredentialLifecycleMutationPhase.LocatorPrepared => mutation.Kind == CredentialRegistryMutationKind.Register && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import && mutation.Health == CredentialProviderHealthStatus.NeedsRepair,
            CredentialLifecycleMutationPhase.LocatorUncertain => mutation.Kind == CredentialRegistryMutationKind.RecordLocatorUncertain && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import,
            CredentialLifecycleMutationPhase.Complete => mutation.Kind == CredentialRegistryMutationKind.SetHealth && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import or (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && mutation.Health == CredentialProviderHealthStatus.Available,
            CredentialLifecycleMutationPhase.Rollback => mutation.Kind == CredentialRegistryMutationKind.SetHealth && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import or (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && mutation.Health is not null and not CredentialProviderHealthStatus.NeedsRepair,
            CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain => mutation.Kind == CredentialRegistryMutationKind.Tombstone && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Delete,
            CredentialLifecycleMutationPhase.RepairComplete => mutation.Kind is CredentialRegistryMutationKind.CompleteRepair or CredentialRegistryMutationKind.Tombstone && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair,
            CredentialLifecycleMutationPhase.Uncertain => mutation.Kind == CredentialRegistryMutationKind.SetHealth && mutation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import or (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && mutation.Health == CredentialProviderHealthStatus.NeedsRepair,
            CredentialLifecycleMutationPhase.RepairUncertain => mutation.Kind is CredentialRegistryMutationKind.RecordRepairUncertain or CredentialRegistryMutationKind.Tombstone && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair,
            CredentialLifecycleMutationPhase.RepairReconciledUncertain => mutation.Kind == CredentialRegistryMutationKind.ReconcileRepair && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.ReconcileRepair && mutation.PreviewHash is not null,
            CredentialLifecycleMutationPhase.MetadataComplete => mutation.OperationId.Equals(mutation.LifecycleIntentOperationId) && (mutation.Kind == CredentialRegistryMutationKind.Bind && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Bind || mutation.Kind == CredentialRegistryMutationKind.Consent && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Consent || mutation.Kind == CredentialRegistryMutationKind.SetHealth && mutation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Test || mutation.Kind == CredentialRegistryMutationKind.UpdatePosture && HasExactMetadataPosture(mutation)),
            _ => false
        };
    }

    private static bool ValidateLifecyclePhaseAgainstState(State current, CredentialRegistryMutation mutation)
    {
        if (mutation.LifecyclePhase is null or CredentialLifecycleMutationPhase.Intent)
        {
            return true;
        }
        if (mutation.LifecyclePhase == CredentialLifecycleMutationPhase.MetadataComplete)
        {
            return mutation.OperationId.Equals(mutation.LifecycleIntentOperationId);
        }
        var intentOperationId = mutation.LifecycleIntentOperationId;
        if (intentOperationId is null)
        {
            return false;
        }
        var intent = current.Public.Operations.SingleOrDefault(operation => string.Equals(operation.OperationId, intentOperationId.Value, StringComparison.Ordinal));
        var latest = current.Public.Operations.LastOrDefault(operation => string.Equals(operation.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
        if (mutation.LifecyclePhase == CredentialLifecycleMutationPhase.RepairReconciledUncertain)
        {
            var existing = current.Public.Entries.SingleOrDefault(entry => Matches(entry, mutation.ReferenceId));
            var repair = current.Public.Tombstones.SingleOrDefault(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
            var repairRequiredState = IsPreparedCreateState(current.Public, existing, current.Private.Locators, mutation.ReferenceId) || existing is null && repair is { NeedsRepair: true } && current.Private.Locators.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && item.RepairRequired);
            return repairRequiredState && intent is not null && latest is not null && intent.Kind == (int)CredentialRegistryMutationKind.BeginRepair && intent.LifecyclePhase == (int)CredentialLifecycleMutationPhase.Intent && intent.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair && string.Equals(intent.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) && string.Equals(intent.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal) && string.Equals(intent.WorkspaceId, mutation.WorkspaceId, StringComparison.Ordinal) && string.Equals(latest.OperationId, intent.OperationId, StringComparison.Ordinal) && !current.Public.Operations.Any(operation => string.Equals(operation.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) && operation.LifecyclePhase is (int)CredentialLifecycleMutationPhase.RepairComplete or (int)CredentialLifecycleMutationPhase.RepairUncertain or (int)CredentialLifecycleMutationPhase.RepairReconciledUncertain);
        }
        if (intent is null || latest is null || intent.LifecyclePhase != (int)CredentialLifecycleMutationPhase.Intent || !string.Equals(intent.LifecycleIntentOperationId, intentOperationId.Value, StringComparison.Ordinal) || intent.ReferenceId != mutation.ReferenceId.Value || intent.LifecycleOperation != mutation.LifecycleOperation || !string.Equals(intent.ActorId, mutation.ActorId, StringComparison.Ordinal) || !string.Equals(intent.WorkspaceId, mutation.WorkspaceId, StringComparison.Ordinal) || !string.Equals(intent.PreviewHash, mutation.PreviewHash, StringComparison.Ordinal) || !string.Equals(intent.LifecycleRequestHash, mutation.LifecycleRequestHash, StringComparison.Ordinal))
        {
            return false;
        }
        var phase = mutation.LifecyclePhase!.Value;
        var createIntent = intent.Kind == (int)CredentialRegistryMutationKind.BeginCreate && intent.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair;
        var latestIsExpected = phase == CredentialLifecycleMutationPhase.LocatorPrepared
            ? string.Equals(latest.OperationId, intent.OperationId, StringComparison.Ordinal)
            : phase == CredentialLifecycleMutationPhase.LocatorUncertain
                ? string.Equals(latest.OperationId, intent.OperationId, StringComparison.Ordinal) || latest.LifecyclePhase == (int)CredentialLifecycleMutationPhase.LocatorPrepared && string.Equals(latest.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal)
            : createIntent ? latest.LifecyclePhase == (int)CredentialLifecycleMutationPhase.LocatorPrepared && string.Equals(latest.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) : string.Equals(latest.OperationId, intent.OperationId, StringComparison.Ordinal);
        if (!latestIsExpected)
        {
            return false;
        }
        return phase switch
        {
            CredentialLifecycleMutationPhase.LocatorPrepared or CredentialLifecycleMutationPhase.LocatorUncertain => createIntent,
            CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.RepairUncertain => intent.Kind == (int)CredentialRegistryMutationKind.BeginRepair && intent.ResultEntry is null,
            CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain => intent.Kind == (int)CredentialRegistryMutationKind.UpdatePosture && intent.LifecycleOperation == (int)CredentialLifecycleOperationKind.Delete && intent.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair,
            _ => createIntent || intent.Kind == (int)CredentialRegistryMutationKind.UpdatePosture && intent.LifecycleOperation is (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && intent.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair
        };
    }

    private static bool HasExactMetadataPosture(CredentialRegistryMutation mutation)
    {
        return mutation.LifecycleOperation switch
        {
            (int)CredentialLifecycleOperationKind.Expire => mutation.Reference?.Status == CredentialLifecycleStatus.Expired && mutation.Health == CredentialProviderHealthStatus.Expired,
            (int)CredentialLifecycleOperationKind.Revoke => mutation.Reference?.Status == CredentialLifecycleStatus.Revoked && mutation.Health == CredentialProviderHealthStatus.Revoked,
            (int)CredentialLifecycleOperationKind.Disable => mutation.Reference?.Status == CredentialLifecycleStatus.Disabled && mutation.Health == CredentialProviderHealthStatus.Disabled,
            _ => false
        };
    }

    private static bool IsPreparedCreateState(CredentialRegistryDocument document, CredentialRegistryEntryDocument? entry, IReadOnlyList<CredentialRegistryLocatorDocument> locators, CredentialReferenceId referenceId)
    {
        if (entry is null || (CredentialProviderHealthStatus)entry.Health != CredentialProviderHealthStatus.NeedsRepair || !locators.Any(locator => string.Equals(locator.ReferenceId, referenceId.Value, StringComparison.Ordinal) && !locator.RepairRequired))
        {
            return false;
        }
        var intent = document.Operations.SingleOrDefault(operation => string.Equals(operation.ReferenceId, referenceId.Value, StringComparison.Ordinal) && operation.Kind == (int)CredentialRegistryMutationKind.BeginCreate && operation.LifecyclePhase == (int)CredentialLifecycleMutationPhase.Intent);
        return intent is not null && document.Operations.Any(operation => string.Equals(operation.ReferenceId, referenceId.Value, StringComparison.Ordinal) && operation.Kind == (int)CredentialRegistryMutationKind.Register && operation.LifecyclePhase == (int)CredentialLifecycleMutationPhase.LocatorPrepared && string.Equals(operation.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal)) && !document.Operations.Any(operation => string.Equals(operation.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) && operation.LifecyclePhase is (int)CredentialLifecycleMutationPhase.Complete or (int)CredentialLifecycleMutationPhase.Rollback);
    }

    private static bool HasUnresolvedRepairIntent(CredentialRegistryDocument document, CredentialReferenceId referenceId, CredentialContractId? operationId = null)
    {
        return document.Operations.Where(operation => string.Equals(operation.ReferenceId, referenceId.Value, StringComparison.Ordinal) && operation.Kind == (int)CredentialRegistryMutationKind.BeginRepair && operation.LifecyclePhase == (int)CredentialLifecycleMutationPhase.Intent && (operationId is null || string.Equals(operation.OperationId, operationId.Value, StringComparison.Ordinal))).Any(intent => !document.Operations.Any(operation => string.Equals(operation.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) && operation.LifecyclePhase is (int)CredentialLifecycleMutationPhase.RepairComplete or (int)CredentialLifecycleMutationPhase.RepairUncertain or (int)CredentialLifecycleMutationPhase.RepairReconciledUncertain));
    }

    private static bool ValidateTombstone(CredentialRegistryTombstoneDocument item, long registryRevision, IReadOnlySet<string> entryIds, ISet<string> tombstoneIds)
    {
        if (!CredentialReferenceId.TryParse(item.ReferenceId, out var referenceId, out _) || item.Revision < 1 || item.Revision > registryRevision || !CredentialContractId.TryParse(item.OperationId, out _, out _) || !CredentialContractHash.TryParse(item.ReferenceHash, out _, out _) || item.TombstonedAtUtc.Offset != TimeSpan.Zero || !tombstoneIds.Add(item.ReferenceId) || entryIds.Contains(item.ReferenceId))
        {
            return false;
        }
        var hasRepairMetadata = item.RepairBindingJson is not null || item.RepairProviderId is not null;
        if (!hasRepairMetadata)
        {
            return !item.NeedsRepair;
        }
        return item.NeedsRepair && item.RepairBindingJson is not null && item.RepairProviderId is not null && CredentialContractJson.TryDeserializeBinding(item.RepairBindingJson, out var binding, out _) && binding!.ReferenceId.Equals(referenceId) && CredentialProviderId.TryParse(item.RepairProviderId, out var providerId, out _) && string.Equals(providerId!.Value, binding.Implementation.ProviderId.Value, StringComparison.Ordinal);
    }

    private static bool IsTerminalPhase(CredentialLifecycleMutationPhase? phase) => phase is CredentialLifecycleMutationPhase.Complete or CredentialLifecycleMutationPhase.Rollback or CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain or CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.Uncertain or CredentialLifecycleMutationPhase.RepairUncertain or CredentialLifecycleMutationPhase.LocatorUncertain or CredentialLifecycleMutationPhase.RepairReconciledUncertain or CredentialLifecycleMutationPhase.MetadataComplete;

    private static string? ExpectedAuditAction(CredentialLifecycleMutationPhase? phase) => phase == CredentialLifecycleMutationPhase.Intent ? AuditSchema.Actions.CredentialLifecycleIntent : IsTerminalPhase(phase) ? AuditSchema.Actions.CredentialLifecycleOutcome : null;

    private static string? ExpectedAuditOutcome(CredentialLifecycleMutationPhase? phase, int? lifecycleOperation, CredentialProviderHealthStatus? health) => phase == CredentialLifecycleMutationPhase.Intent ? AuditSchema.Outcomes.Started : phase == CredentialLifecycleMutationPhase.MetadataComplete && lifecycleOperation == (int)CredentialLifecycleOperationKind.Test && health is CredentialProviderHealthStatus.Unavailable or CredentialProviderHealthStatus.Corrupt ? AuditSchema.Outcomes.Failed : phase is CredentialLifecycleMutationPhase.Complete or CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.MetadataComplete ? AuditSchema.Outcomes.Succeeded : IsTerminalPhase(phase) ? AuditSchema.Outcomes.Failed : null;

    private static bool IsSafe(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && value.All(character => character >= (char)0x20 && character != (char)0x7f);

    private static bool IsRestrictive(CredentialRegistryEntryDocument document)
    {
        var referenceIsRestrictive = CredentialContractJson.TryDeserializeReference(document.ReferenceJson, out var reference, out _) && reference!.Status is CredentialLifecycleStatus.Revoked or CredentialLifecycleStatus.Disabled or CredentialLifecycleStatus.Expired;
        var healthIsRestrictive = (CredentialProviderHealthStatus)document.Health is CredentialProviderHealthStatus.Revoked or CredentialProviderHealthStatus.Disabled or CredentialProviderHealthStatus.Expired;
        return referenceIsRestrictive || healthIsRestrictive;
    }

    private bool ValidatePair(CredentialRegistryDocument publicDocument, CredentialRegistryPrivateDocument privateDocument, string identity)
    {
        if (publicDocument.SchemaVersion != CredentialRegistryDocument.CurrentSchemaVersion || publicDocument.LifecycleShape != CredentialRegistryDocument.CurrentLifecycleShape || privateDocument.SchemaVersion != CredentialRegistryPrivateDocument.CurrentSchemaVersion || publicDocument.Revision < 0 || privateDocument.Revision != publicDocument.Revision || !string.Equals(publicDocument.WorkspaceIdentity, identity, StringComparison.Ordinal) || !string.Equals(privateDocument.WorkspaceIdentity, identity, StringComparison.Ordinal) || string.IsNullOrEmpty(publicDocument.AuthenticationTag) || _utf8.GetByteCount(publicDocument.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes || publicDocument.Entries is null || publicDocument.Tombstones is null || publicDocument.Operations is null || publicDocument.Evidence is null || publicDocument.AuditDeliveries is null || publicDocument.EvidenceReservations is null || privateDocument.Locators is null || publicDocument.Entries.Count > _quota.MaximumEntries || publicDocument.Tombstones.Count > _quota.MaximumTombstones || publicDocument.Operations.Count + publicDocument.EvidenceReservations.Count > _quota.MaximumOperations || publicDocument.Evidence.Count + publicDocument.EvidenceReservations.Count > _quota.MaximumEvidence || publicDocument.AuditDeliveries.Count > publicDocument.Operations.Count)
        {
            return false;
        }

        var expected = StateDigest(publicDocument with { StateDigest = string.Empty, ContentDigest = string.Empty, AuthenticationTag = string.Empty }, privateDocument with { StateDigest = string.Empty });
        if (!FixedTimeEquals(publicDocument.StateDigest, expected) || !FixedTimeEquals(privateDocument.StateDigest, expected))
        {
            return false;
        }

        if (!FixedTimeEquals(publicDocument.ContentDigest, ComputeDocumentDigest(publicDocument)))
        {
            return false;
        }

        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in publicDocument.Entries)
        {
            if (!TryMapEntry(entry, out var mapped) || mapped!.Revision > publicDocument.Revision || !entryIds.Add(mapped.Reference.Id.Value) || !privateDocument.Locators.Any(locator => string.Equals(locator.ReferenceId, mapped.Reference.Id.Value, StringComparison.Ordinal) && !locator.RepairRequired))
            {
                return false;
            }
        }

        if (!publicDocument.Entries.Select(GetReferenceId).SequenceEqual(publicDocument.Entries.Select(GetReferenceId).Order(StringComparer.Ordinal), StringComparer.Ordinal) || !privateDocument.Locators.Select(item => item.ReferenceId).SequenceEqual(privateDocument.Locators.Select(item => item.ReferenceId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        var tombstoneIds = new HashSet<string>(StringComparer.Ordinal);
        if (publicDocument.Tombstones.Any(item => !ValidateTombstone(item, publicDocument.Revision, entryIds, tombstoneIds)))
        {
            return false;
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        if (publicDocument.Operations.Any(item => !CredentialContractId.TryParse(item.OperationId, out _, out _) || !CredentialContractHash.TryParse(item.RequestHash, out _, out _) || item.Revision < 1 || item.Revision > publicDocument.Revision || !operationIds.Add(item.OperationId) || !CredentialReferenceId.TryParse(item.ReferenceId, out _, out _) || item.ResultEntry is not null && !TryMapEntry(item.ResultEntry, out _) || item.LifecycleOperation is < 1 or > 13 || item.ActorId is not null && !IsSafe(item.ActorId, 128) || item.WorkspaceId is not null && !IsSafe(item.WorkspaceId, 256) || item.PreviewHash is not null && !CredentialContractHash.TryParse(item.PreviewHash, out _, out _) || item.LifecycleRequestHash is not null && !CredentialContractHash.TryParse(item.LifecycleRequestHash, out _, out _) || item.LifecyclePhase is not null && !Enum.IsDefined((CredentialLifecycleMutationPhase)item.LifecyclePhase.Value) || item.LifecyclePhase is null != (item.LifecycleIntentOperationId is null) || item.LifecyclePhase is not null && item.WorkspaceId is null || item.LifecycleIntentOperationId is not null && !CredentialContractId.TryParse(item.LifecycleIntentOperationId, out _, out _) || !ValidateActiveRuns(item.AffectedActiveRuns) || !ValidateAuditOutbox(item)))
        {
            return false;
        }

        if (!ValidatePersistedLifecyclePhases(publicDocument.Operations))
        {
            return false;
        }

        if (publicDocument.Tombstones.Any(tombstone => !publicDocument.Operations.Any(operation => string.Equals(operation.OperationId, tombstone.OperationId, StringComparison.Ordinal) && string.Equals(operation.ReferenceId, tombstone.ReferenceId, StringComparison.Ordinal) && (tombstone.NeedsRepair ? operation.Kind == (int)CredentialRegistryMutationKind.Tombstone && operation.LifecyclePhase is (int)CredentialLifecycleMutationPhase.TombstoneUncertain or (int)CredentialLifecycleMutationPhase.RepairUncertain || operation.Kind == (int)CredentialRegistryMutationKind.ReconcileRepair && operation.LifecyclePhase == (int)CredentialLifecycleMutationPhase.RepairReconciledUncertain : operation.Kind == (int)CredentialRegistryMutationKind.Tombstone && operation.LifecyclePhase is null or (int)CredentialLifecycleMutationPhase.TombstoneComplete or (int)CredentialLifecycleMutationPhase.RepairComplete))))
        {
            return false;
        }

        var deliveredIds = new HashSet<string>(StringComparer.Ordinal);
        if (publicDocument.AuditDeliveries.Any(item => !CredentialContractId.TryParse(item.TerminalOperationId, out _, out _) || item.DeliveredAtUtc.Offset != TimeSpan.Zero || !deliveredIds.Add(item.TerminalOperationId) || !publicDocument.Operations.Any(operation => operation.AuditOutbox is not null && string.Equals(operation.OperationId, item.TerminalOperationId, StringComparison.Ordinal))))
        {
            return false;
        }

        var completedRepairs = publicDocument.Operations.Where(item => item.LifecyclePhase == (int)CredentialLifecycleMutationPhase.RepairComplete).ToArray();
        var completedRepairIds = completedRepairs.Select(item => item.ReferenceId).ToArray();
        if (completedRepairIds.Distinct(StringComparer.Ordinal).Count() != completedRepairIds.Length || completedRepairs.Any(repair => !publicDocument.Tombstones.Any(item => string.Equals(item.ReferenceId, repair.ReferenceId, StringComparison.Ordinal) && (item.NeedsRepair || repair.Kind == (int)CredentialRegistryMutationKind.Tombstone && string.Equals(item.OperationId, repair.OperationId, StringComparison.Ordinal)))))
        {
            return false;
        }
        var completedRepairSet = completedRepairIds.ToHashSet(StringComparer.Ordinal);
        var unresolvedRepairIds = publicDocument.Tombstones.Where(item => item.NeedsRepair && !completedRepairSet.Contains(item.ReferenceId)).Select(item => item.ReferenceId).ToHashSet(StringComparer.Ordinal);
        var locatorIds = new HashSet<string>(StringComparer.Ordinal);
        if (privateDocument.Locators.Any(item => !locatorIds.Add(item.ReferenceId) || !CredentialProviderLocator.TryParse(item.Locator, out _) || (item.RepairRequired ? !unresolvedRepairIds.Contains(item.ReferenceId) : !entryIds.Contains(item.ReferenceId))) || unresolvedRepairIds.Any(referenceId => !privateDocument.Locators.Any(locator => locator.RepairRequired && string.Equals(locator.ReferenceId, referenceId, StringComparison.Ordinal))) || completedRepairSet.Any(referenceId => privateDocument.Locators.Any(locator => string.Equals(locator.ReferenceId, referenceId, StringComparison.Ordinal))))
        {
            return false;
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        if (!publicDocument.Evidence.All(item => CredentialContractJson.TryDeserializeEvidence(item.EvidenceJson, out var evidence, out _) && evidenceIds.Add(evidence!.EvidenceId.Value)))
        {
            return false;
        }

        return publicDocument.EvidenceReservations.All(item => ValidateReservation(item, evidenceIds, operationIds));
    }

    private static bool ValidateAuditOutbox(CredentialRegistryOperationDocument operation)
    {
        var phase = operation.LifecyclePhase is null ? (CredentialLifecycleMutationPhase?)null : (CredentialLifecycleMutationPhase)operation.LifecyclePhase.Value;
        var audited = phase == CredentialLifecycleMutationPhase.Intent || IsTerminalPhase(phase);
        if (audited != (operation.AuditOutbox is not null))
        {
            return false;
        }
        var health = operation.ResultEntry is null ? null : (CredentialProviderHealthStatus?)operation.ResultEntry.Health;
        return operation.AuditOutbox is null || operation.AuditOutbox.OccurredAtUtc.Offset == TimeSpan.Zero && operation.AuditOutbox.RegistryRevision == operation.Revision && string.Equals(operation.AuditOutbox.Action, ExpectedAuditAction(phase), StringComparison.Ordinal) && string.Equals(operation.AuditOutbox.Outcome, ExpectedAuditOutcome(phase, operation.LifecycleOperation, health), StringComparison.Ordinal) && IsSafe(operation.AuditOutbox.Detail, 512);
    }

    private static bool ValidatePersistedLifecyclePhases(IReadOnlyList<CredentialRegistryOperationDocument> operations)
    {
        var priorById = new Dictionary<string, CredentialRegistryOperationDocument>(StringComparer.Ordinal);
        var latestByReference = new Dictionary<string, CredentialRegistryOperationDocument>(StringComparer.Ordinal);
        foreach (var operation in operations)
        {
            if (!ValidatePersistedLifecyclePhaseShape(operation))
            {
                return false;
            }
            if (operation.LifecyclePhase is not null and not (int)CredentialLifecycleMutationPhase.Intent)
            {
                if (operation.LifecyclePhase == (int)CredentialLifecycleMutationPhase.MetadataComplete)
                {
                    if (!string.Equals(operation.OperationId, operation.LifecycleIntentOperationId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                    priorById.Add(operation.OperationId, operation);
                    latestByReference[operation.ReferenceId] = operation;
                    continue;
                }
                if (!priorById.TryGetValue(operation.LifecycleIntentOperationId!, out var intent) || !latestByReference.TryGetValue(operation.ReferenceId, out var latestPrior))
                {
                    return false;
                }
                if (operation.LifecyclePhase == (int)CredentialLifecycleMutationPhase.RepairReconciledUncertain)
                {
                    var exactReconciliation = intent.Kind == (int)CredentialRegistryMutationKind.BeginRepair && intent.LifecyclePhase == (int)CredentialLifecycleMutationPhase.Intent && intent.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair && string.Equals(intent.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) && string.Equals(intent.ReferenceId, operation.ReferenceId, StringComparison.Ordinal) && string.Equals(intent.WorkspaceId, operation.WorkspaceId, StringComparison.Ordinal) && string.Equals(latestPrior.OperationId, intent.OperationId, StringComparison.Ordinal);
                    if (!exactReconciliation)
                    {
                        return false;
                    }
                    priorById.Add(operation.OperationId, operation);
                    latestByReference[operation.ReferenceId] = operation;
                    continue;
                }
                var exactCorrelation = intent.LifecyclePhase == (int)CredentialLifecycleMutationPhase.Intent && string.Equals(intent.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) && string.Equals(intent.ReferenceId, operation.ReferenceId, StringComparison.Ordinal) && intent.LifecycleOperation == operation.LifecycleOperation && string.Equals(intent.ActorId, operation.ActorId, StringComparison.Ordinal) && string.Equals(intent.WorkspaceId, operation.WorkspaceId, StringComparison.Ordinal) && string.Equals(intent.PreviewHash, operation.PreviewHash, StringComparison.Ordinal) && string.Equals(intent.LifecycleRequestHash, operation.LifecycleRequestHash, StringComparison.Ordinal);
                if (!exactCorrelation)
                {
                    return false;
                }
                var phase = (CredentialLifecycleMutationPhase)operation.LifecyclePhase.Value;
                var createIntent = intent.Kind == (int)CredentialRegistryMutationKind.BeginCreate && intent.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair;
                var latestIsExpected = phase == CredentialLifecycleMutationPhase.LocatorPrepared
                    ? string.Equals(latestPrior.OperationId, intent.OperationId, StringComparison.Ordinal)
                    : phase == CredentialLifecycleMutationPhase.LocatorUncertain
                        ? string.Equals(latestPrior.OperationId, intent.OperationId, StringComparison.Ordinal) || latestPrior.LifecyclePhase == (int)CredentialLifecycleMutationPhase.LocatorPrepared && string.Equals(latestPrior.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal)
                    : createIntent ? latestPrior.LifecyclePhase == (int)CredentialLifecycleMutationPhase.LocatorPrepared && string.Equals(latestPrior.LifecycleIntentOperationId, intent.OperationId, StringComparison.Ordinal) : string.Equals(latestPrior.OperationId, intent.OperationId, StringComparison.Ordinal);
                var correctIntentKind = phase switch
                {
                    CredentialLifecycleMutationPhase.LocatorPrepared or CredentialLifecycleMutationPhase.LocatorUncertain => createIntent,
                    CredentialLifecycleMutationPhase.RepairComplete or CredentialLifecycleMutationPhase.RepairUncertain => intent.Kind == (int)CredentialRegistryMutationKind.BeginRepair && intent.ResultEntry is null,
                    CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain => intent.Kind == (int)CredentialRegistryMutationKind.UpdatePosture && intent.LifecycleOperation == (int)CredentialLifecycleOperationKind.Delete && intent.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair,
                    _ => createIntent || intent.Kind == (int)CredentialRegistryMutationKind.UpdatePosture && intent.LifecycleOperation is (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && intent.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair
                };
                if (!latestIsExpected || !correctIntentKind)
                {
                    return false;
                }
            }
            priorById.Add(operation.OperationId, operation);
            latestByReference[operation.ReferenceId] = operation;
        }
        return true;
    }

    private static bool ValidatePersistedLifecyclePhaseShape(CredentialRegistryOperationDocument operation)
    {
        if (operation.LifecyclePhase is null)
        {
            return true;
        }
        var phase = (CredentialLifecycleMutationPhase)operation.LifecyclePhase.Value;
        return phase switch
        {
            CredentialLifecycleMutationPhase.Intent => string.Equals(operation.OperationId, operation.LifecycleIntentOperationId, StringComparison.Ordinal) && (operation.Kind == (int)CredentialRegistryMutationKind.BeginCreate && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import && operation.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair || operation.Kind == (int)CredentialRegistryMutationKind.UpdatePosture && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace or (int)CredentialLifecycleOperationKind.Delete && operation.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair || operation.Kind == (int)CredentialRegistryMutationKind.BeginRepair && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair && operation.ResultEntry is null),
            CredentialLifecycleMutationPhase.LocatorPrepared => operation.Kind == (int)CredentialRegistryMutationKind.Register && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import && operation.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair,
            CredentialLifecycleMutationPhase.LocatorUncertain => operation.Kind == (int)CredentialRegistryMutationKind.RecordLocatorUncertain && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import && operation.ResultEntry is null,
            CredentialLifecycleMutationPhase.Complete => operation.Kind == (int)CredentialRegistryMutationKind.SetHealth && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import or (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && operation.ResultEntry?.Health == (int)CredentialProviderHealthStatus.Available,
            CredentialLifecycleMutationPhase.Rollback => operation.Kind == (int)CredentialRegistryMutationKind.SetHealth && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import or (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && operation.ResultEntry is not null && operation.ResultEntry.Health != (int)CredentialProviderHealthStatus.NeedsRepair,
            CredentialLifecycleMutationPhase.TombstoneComplete or CredentialLifecycleMutationPhase.TombstoneUncertain => operation.Kind == (int)CredentialRegistryMutationKind.Tombstone && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Delete && operation.ResultEntry is null,
            CredentialLifecycleMutationPhase.RepairComplete => operation.Kind is (int)CredentialRegistryMutationKind.CompleteRepair or (int)CredentialRegistryMutationKind.Tombstone && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair && operation.ResultEntry is null,
            CredentialLifecycleMutationPhase.Uncertain => operation.Kind == (int)CredentialRegistryMutationKind.SetHealth && operation.LifecycleOperation is (int)CredentialLifecycleOperationKind.Create or (int)CredentialLifecycleOperationKind.Import or (int)CredentialLifecycleOperationKind.Rotate or (int)CredentialLifecycleOperationKind.Replace && operation.ResultEntry?.Health == (int)CredentialProviderHealthStatus.NeedsRepair,
            CredentialLifecycleMutationPhase.RepairUncertain => operation.Kind is (int)CredentialRegistryMutationKind.RecordRepairUncertain or (int)CredentialRegistryMutationKind.Tombstone && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Repair && operation.ResultEntry is null,
            CredentialLifecycleMutationPhase.RepairReconciledUncertain => operation.Kind == (int)CredentialRegistryMutationKind.ReconcileRepair && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.ReconcileRepair && operation.PreviewHash is not null && operation.ResultEntry is null,
            CredentialLifecycleMutationPhase.MetadataComplete => string.Equals(operation.OperationId, operation.LifecycleIntentOperationId, StringComparison.Ordinal) && (operation.Kind == (int)CredentialRegistryMutationKind.Bind && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Bind || operation.Kind == (int)CredentialRegistryMutationKind.Consent && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Consent || operation.Kind == (int)CredentialRegistryMutationKind.SetHealth && operation.LifecycleOperation == (int)CredentialLifecycleOperationKind.Test || operation.Kind == (int)CredentialRegistryMutationKind.UpdatePosture && HasExactPersistedMetadataPosture(operation)) && operation.ResultEntry is not null,
            _ => false
        };
    }

    private static bool HasExactPersistedMetadataPosture(CredentialRegistryOperationDocument operation)
    {
        if (operation.ResultEntry is null || !TryMapEntry(operation.ResultEntry, out var entry))
        {
            return false;
        }
        return operation.LifecycleOperation switch
        {
            (int)CredentialLifecycleOperationKind.Expire => entry!.Reference.Status == CredentialLifecycleStatus.Expired && entry.Health == CredentialProviderHealthStatus.Expired,
            (int)CredentialLifecycleOperationKind.Revoke => entry!.Reference.Status == CredentialLifecycleStatus.Revoked && entry.Health == CredentialProviderHealthStatus.Revoked,
            (int)CredentialLifecycleOperationKind.Disable => entry!.Reference.Status == CredentialLifecycleStatus.Disabled && entry.Health == CredentialProviderHealthStatus.Disabled,
            _ => false
        };
    }

    private static bool TryMapEntry(CredentialRegistryEntryDocument document, out CredentialRegistryEntry? entry)
    {
        entry = null;
        if (document is null || document.Revision < 1 || !CredentialContractJson.TryDeserializeReference(document.ReferenceJson, out var reference, out _) || !CredentialContractJson.TryDeserializeBinding(document.BindingJson, out var binding, out _) || !CredentialContractJson.TryHash(binding, out var bindingHash, out _) || !bindingHash!.Value.Equals(document.BindingHash, StringComparison.Ordinal) || !CredentialContractId.TryParse(document.ConsentReference, out var consent, out _) || !CredentialContractId.TryParse(document.LastOperationId, out var operation, out _) || !Enum.IsDefined((CredentialProviderHealthStatus)document.Health) || !reference!.Id.Equals(binding!.ReferenceId))
        {
            return false;
        }

        entry = new CredentialRegistryEntry(reference, binding, bindingHash, consent!, (CredentialProviderHealthStatus)document.Health, document.Revision, operation!, document.ConsentGranted);
        return true;
    }

    private static CredentialRegistryEntry? FindEntry(CredentialRegistryDocument document, CredentialReferenceId id)
    {
        var entry = document.Entries.SingleOrDefault(item => Matches(item, id));
        return entry is not null && TryMapEntry(entry, out var mapped) ? mapped : null;
    }

    private static CredentialRegistryReadResult ToReadResult(State state)
    {
        var entries = state.Public.Entries.Select(item => TryMapEntry(item, out var mapped) ? mapped! : throw new FormatException()).ToArray();
        var completedRepairIds = state.Public.Operations.Where(item => item.LifecyclePhase == (int)CredentialLifecycleMutationPhase.RepairComplete).Select(item => item.ReferenceId).ToHashSet(StringComparer.Ordinal);
        var tombstones = state.Public.Tombstones.Select(item => MapTombstone(item, completedRepairIds.Contains(item.ReferenceId))).ToArray();
        var operations = state.Public.Operations.Select(item => new CredentialRegistryOperationEvidence(ParseContractId(item.OperationId), ParseHash(item.RequestHash), item.Kind, item.Revision, ParseReferenceId(item.ReferenceId), item.LifecycleOperation, item.ActorId, item.PreviewHash, item.LifecycleRequestHash, item.LifecyclePhase is null ? null : (CredentialLifecycleMutationPhase)item.LifecyclePhase.Value, item.LifecycleIntentOperationId is null ? null : ParseContractId(item.LifecycleIntentOperationId), item.ResultEntry is null ? null : (CredentialProviderHealthStatus)item.ResultEntry.Health, item.AffectedActiveRuns, item.WorkspaceId)).ToArray();
        var evidence = state.Public.Evidence.Select(item => CredentialContractJson.TryDeserializeEvidence(item.EvidenceJson, out var mapped, out _) ? mapped! : throw new FormatException()).ToArray();
        var delivered = state.Public.AuditDeliveries!.Select(item => item.TerminalOperationId).ToHashSet(StringComparer.Ordinal);
        var pendingAudits = state.Public.Operations.Where(item => item.AuditOutbox is not null && !delivered.Contains(item.OperationId)).Select(MapAuditOutbox).OrderBy(item => item.RegistryRevision).ThenBy(item => item.AuditOperationId.Value, StringComparer.Ordinal).ToArray();
        return new CredentialRegistryReadResult(state.Public.Revision, entries, tombstones, operations, evidence, null, pendingAudits);
    }

    private static CredentialRegistryTombstone MapTombstone(CredentialRegistryTombstoneDocument item, bool repairCompleted)
    {
        CredentialCapabilityBinding? repairBinding = null;
        if (item.RepairBindingJson is not null && !CredentialContractJson.TryDeserializeBinding(item.RepairBindingJson, out repairBinding, out _))
        {
            throw new FormatException();
        }
        var providerId = item.RepairProviderId is null ? null : CredentialProviderId.TryParse(item.RepairProviderId, out var parsed, out _) ? parsed : throw new FormatException();
        return new CredentialRegistryTombstone(ParseReferenceId(item.ReferenceId), item.Revision, ParseContractId(item.OperationId), item.TombstonedAtUtc, ParseHash(item.ReferenceHash), item.NeedsRepair && !repairCompleted, repairBinding, providerId);
    }

    private static CredentialLifecycleAuditOutboxItem MapAuditOutbox(CredentialRegistryOperationDocument item)
    {
        var audit = item.AuditOutbox ?? throw new FormatException();
        return new CredentialLifecycleAuditOutboxItem(ParseContractId(item.OperationId), ParseContractId(item.LifecycleIntentOperationId!), ParseReferenceId(item.ReferenceId), item.WorkspaceId!, item.ActorId!, (CredentialLifecycleOperationKind)item.LifecycleOperation!.Value, audit.OccurredAtUtc, audit.RegistryRevision, item.PreviewHash, audit.Action, audit.Outcome, audit.Detail);
    }

    private static State Empty(string identity) => Complete(identity, new CredentialRegistryDocument(1, 1, identity, 0, 0, [], [], [], [], string.Empty, string.Empty, string.Empty, [], []), new CredentialRegistryPrivateDocument(1, identity, 0, [], string.Empty));

    private static State Complete(string identity, CredentialRegistryDocument publicDocument, CredentialRegistryPrivateDocument privateDocument)
    {
        var digest = StateDigest(publicDocument with { WorkspaceIdentity = identity, StateDigest = string.Empty, ContentDigest = string.Empty, AuthenticationTag = string.Empty }, privateDocument with { WorkspaceIdentity = identity, StateDigest = string.Empty });
        return new State(identity, publicDocument with { WorkspaceIdentity = identity, StateDigest = digest, ContentDigest = string.Empty, AuthenticationTag = string.Empty }, privateDocument with { WorkspaceIdentity = identity, StateDigest = digest }, false);
    }

    private static string StateDigest(CredentialRegistryDocument publicDocument, CredentialRegistryPrivateDocument privateDocument) => Hash(JsonSerializer.Serialize(publicDocument, _canonicalJson) + "\n" + JsonSerializer.Serialize(privateDocument, _canonicalJson));
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedTimeEquals(string? left, string right) => left is not null && left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static string WorkspaceIdentity(string material) => Hash("embodysense-credential-registry-workspace-v1\n" + material);
    private static bool IsInitialEmptyTrust(string identity, CapabilityCatalogTrustState trust)
    {
        var empty = Empty(identity).Public;
        return trust.CurrentGeneration == empty.Generation && trust.PreviousGeneration is null && trust.PreviousContentDigest is null && MatchesCurrent(empty with { ContentDigest = ComputeDocumentDigest(empty) }, trust);
    }
    private static bool MatchesCurrent(CredentialRegistryDocument document, CapabilityCatalogTrustState trust) => document.Generation == trust.CurrentGeneration && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);
    private static bool MatchesPrevious(CredentialRegistryDocument document, CapabilityCatalogTrustState trust) => trust.PreviousGeneration == document.Generation && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);
    private static bool Matches(CredentialRegistryEntryDocument document, CredentialReferenceId id) => string.Equals(GetReferenceId(document), id.Value, StringComparison.Ordinal);
    private static string GetReferenceId(CredentialRegistryEntryDocument document) => CredentialContractJson.TryDeserializeReference(document.ReferenceJson, out var reference, out _) ? reference!.Id.Value : throw new FormatException();
    private static bool HasEvidenceId(CredentialRegistryEvidenceDocument document, CredentialContractId id) => CredentialContractJson.TryDeserializeEvidence(document.EvidenceJson, out var evidence, out _) && evidence!.EvidenceId.Equals(id);
    private static bool ReservationMatches(CredentialRegistryEvidenceReservationDocument reservation, CredentialLeaseIntent intent)
        => string.Equals(reservation.EvidenceId, CredentialLeaseContract.ComputeEvidenceId(intent.CredentialUseOperationId, intent.CredentialUseGeneration).Value, StringComparison.Ordinal)
            && string.Equals(reservation.CredentialUseOperationId, intent.CredentialUseOperationId, StringComparison.Ordinal)
            && reservation.CredentialUseGeneration == intent.CredentialUseGeneration
            && string.Equals(reservation.IntentHash, intent.ContentHash, StringComparison.Ordinal)
            && string.Equals(reservation.ReferenceId, intent.Registry.ReferenceId, StringComparison.Ordinal)
            && string.Equals(reservation.BindingHash, intent.Registry.BindingHash, StringComparison.Ordinal);
    private static bool ValidateReservation(CredentialRegistryEvidenceReservationDocument reservation, HashSet<string> evidenceIds, HashSet<string> operationIds)
    {
        if (!CredentialContractId.TryParse(reservation.EvidenceId, out var evidenceId, out _)
            || !CredentialContractId.TryParse(reservation.CredentialUseOperationId, out _, out _)
            || reservation.CredentialUseGeneration < 1
            || !CredentialReferenceId.TryParse(reservation.ReferenceId, out _, out _)
            || !CredentialContractHash.TryParse(reservation.IntentHash, out _, out _)
            || !CredentialContractHash.TryParse(reservation.BindingHash, out _, out _)
            || !evidenceId!.Equals(CredentialLeaseContract.ComputeEvidenceId(reservation.CredentialUseOperationId, reservation.CredentialUseGeneration))
            || evidenceIds.Contains(reservation.EvidenceId)
            || operationIds.Contains(reservation.EvidenceId))
        {
            return false;
        }

        return evidenceIds.Add(reservation.EvidenceId);
    }
    private static CredentialReferenceId ParseReferenceId(string value) => CredentialReferenceId.TryParse(value, out var parsed, out _) ? parsed! : throw new FormatException();
    private static CredentialContractId ParseContractId(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new FormatException();
    private static CredentialContractHash ParseHash(string value) => CredentialContractHash.TryParse(value, out var parsed, out _) ? parsed! : throw new FormatException();

    private bool TryGetUtcNow(out DateTimeOffset nowUtc)
    {
        try
        {
            nowUtc = _timeProvider.GetUtcNow();
            return nowUtc != default && nowUtc.Offset == TimeSpan.Zero;
        }
        catch (Exception)
        {
            nowUtc = default;
            return false;
        }
    }
    private static CredentialRegistryReadResult FailedRead(CredentialFailureCode code) => new(null, [], [], [], [], CredentialFailure.FromCode(code));
    private static CredentialRegistryMutationResult Mutation(CredentialRegistryMutationStatus status, CredentialContractId? operationId, long? revision, CredentialRegistryEntry? entry, CredentialFailureCode? failure) => new(status, operationId ?? ParseContractId("invalid"), revision, entry, failure is null ? null : CredentialFailure.FromCode(failure.Value));
    private static bool IsStorageFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or FormatException or JsonException or OverflowException or ArgumentException;
    private static CredentialRegistryQuota ValidateQuota(CredentialRegistryQuota? quota)
    {
        var effective = quota ?? new CredentialRegistryQuota(CredentialRegistryLimits.MaximumEntries, CredentialRegistryLimits.MaximumTombstones, CredentialRegistryLimits.MaximumOperations, CredentialRegistryLimits.MaximumEvidence, CredentialRegistryLimits.MaximumArtifactUtf8Bytes);
        if (effective.MaximumEntries < 1 || effective.MaximumEntries > CredentialRegistryLimits.MaximumEntries || effective.MaximumTombstones < 1 || effective.MaximumTombstones > CredentialRegistryLimits.MaximumTombstones || effective.MaximumOperations < 1 || effective.MaximumOperations > CredentialRegistryLimits.MaximumOperations || effective.MaximumEvidence < 1 || effective.MaximumEvidence > CredentialRegistryLimits.MaximumEvidence || effective.MaximumArtifactUtf8Bytes < 1 || effective.MaximumArtifactUtf8Bytes > CredentialRegistryLimits.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(quota));
        }

        return effective;
    }
    private sealed record State(string WorkspaceIdentity, CredentialRegistryDocument Public, CredentialRegistryPrivateDocument Private, bool Recovered);
    private sealed record SerializedState(string PublicJson, string PrivateJson, string ContentDigest);
}
