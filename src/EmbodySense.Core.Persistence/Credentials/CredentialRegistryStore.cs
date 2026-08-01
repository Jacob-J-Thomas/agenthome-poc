using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Credentials;
using EmbodySense.Core.Application.Credentials.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Credentials;
using EmbodySense.Core.Common.Credentials.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.Credentials.Models;

namespace EmbodySense.Core.Persistence.Credentials;

/// <summary>Persists schema-1, workspace-bound, value-free credential registry state and immutable evidence.</summary>
/// <remarks>Provider locators are strictly shaped opaque tokens retained only beneath the workspace private boundary. This store never resolves a locator or accepts credential bytes, encrypted envelopes, or key material.</remarks>
public sealed class CredentialRegistryStore : ICredentialRegistryStore
{
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

    /// <inheritdoc />
    public async Task<CredentialRegistryMutationResult> MutateAsync(CredentialRegistryMutation mutation, CancellationToken cancellationToken = default)
    {
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

            if (!await VerifyLocatorAsync(current, mutation, cancellationToken))
            {
                return Mutation(CredentialRegistryMutationStatus.Unavailable, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Unavailable);
            }

            if (mutation.ExpectedRegistryRevision != current.Public.Revision)
            {
                return Mutation(CredentialRegistryMutationStatus.Conflict, mutation.OperationId, current.Public.Revision, null, CredentialFailureCode.Conflict);
            }

            if (current.Public.Operations.Count >= _quota.MaximumOperations || mutation.Kind == CredentialRegistryMutationKind.Register && current.Public.Entries.Count >= _quota.MaximumEntries || mutation.Kind == CredentialRegistryMutationKind.Tombstone && current.Public.Tombstones.Count >= _quota.MaximumTombstones)
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

            var existing = current.Public.Evidence.SingleOrDefault(item => HasEvidenceId(item, evidence!.EvidenceId));
            if (existing is not null)
            {
                return string.Equals(existing.EvidenceJson, evidenceJson, StringComparison.Ordinal) ? CredentialEvidenceWriteResult.Success() : CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            var reference = FindEntry(current.Public, evidence.ReferenceId);
            if (reference is null)
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.NotFound));
            }

            if (!reference.BindingHash.FixedTimeEquals(evidence.BindingHash))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Conflict));
            }

            if (!CredentialScopeRules.IsNarrowerThanOrEqual(evidence.UsedScope, reference.Binding.Scope))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.Unauthorized));
            }

            if (current.Public.Evidence.Count >= _quota.MaximumEvidence || current.Public.Operations.Count >= _quota.MaximumOperations || current.Public.Operations.Any(item => string.Equals(item.OperationId, evidence!.EvidenceId.Value, StringComparison.Ordinal)))
            {
                return CredentialEvidenceWriteResult.Failed(CredentialFailure.FromCode(CredentialFailureCode.LimitExceeded));
            }

            var revision = checked(current.Public.Revision + 1);
            var operation = new CredentialRegistryOperationDocument(evidence!.EvidenceId.Value, Hash("evidence\n" + evidenceJson), -1, revision, evidence.ReferenceId.Value, null);
            var publicCandidate = current.Public with { Generation = checked(current.Public.Generation + 1), Revision = revision, Evidence = [.. current.Public.Evidence, new CredentialRegistryEvidenceDocument(evidenceJson!)], Operations = [.. current.Public.Operations, operation] };
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
        if (mutation.Kind == CredentialRegistryMutationKind.Register)
        {
            if (existing is not null || tombstones.Any(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal)))
            {
                return null;
            }

            CredentialContractJson.TrySerialize(mutation.Reference, out var referenceJson, out _);
            CredentialContractJson.TrySerialize(mutation.Binding, out var bindingJson, out _);
            CredentialContractJson.TryHash(mutation.Binding, out var bindingHash, out _);
            entries.Add(new CredentialRegistryEntryDocument(referenceJson!, bindingJson!, bindingHash!.Value, mutation.ConsentReference!.Value, (int)mutation.Health!.Value, revision, mutation.OperationId.Value));
            locators.Add(new CredentialRegistryLocatorDocument(mutation.ReferenceId.Value, mutation.ProviderLocator!.Value));
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.SetHealth)
        {
            if (existing is null)
            {
                return null;
            }

            entries[entries.IndexOf(existing)] = existing with { Health = (int)mutation.Health!.Value, Revision = revision, LastOperationId = mutation.OperationId.Value };
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.Tombstone)
        {
            if (existing is null || !CredentialContractJson.TryDeserializeReference(existing.ReferenceJson, out var reference, out _) || !CredentialContractJson.TryHash(reference, out var referenceHash, out _))
            {
                return null;
            }

            entries.Remove(existing);
            locators.RemoveAll(item => string.Equals(item.ReferenceId, mutation.ReferenceId.Value, StringComparison.Ordinal));
            tombstones.Add(new CredentialRegistryTombstoneDocument(mutation.ReferenceId.Value, revision, mutation.OperationId.Value, _timeProvider.GetUtcNow(), referenceHash!.Value));
        }
        else
        {
            return null;
        }

        var resultEntry = mutation.Kind == CredentialRegistryMutationKind.Tombstone ? null : entries.Single(item => Matches(item, mutation.ReferenceId));
        var operation = new CredentialRegistryOperationDocument(mutation.OperationId.Value, requestHash, (int)mutation.Kind, revision, mutation.ReferenceId.Value, resultEntry);
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
        var authenticationTagPlaceholder = new string('\u0001', _trustProvider.MaximumAuthenticationTagUtf8Bytes);
        var digest = ComputeDocumentDigest(state.Public);
        var publicJson = JsonSerializer.Serialize(state.Public with { ContentDigest = digest, AuthenticationTag = authenticationTagPlaceholder }, _json) + Environment.NewLine;
        var privateJson = JsonSerializer.Serialize(state.Private, _json) + Environment.NewLine;
        if (_utf8.GetByteCount(publicJson) > _quota.MaximumArtifactUtf8Bytes || _utf8.GetByteCount(privateJson) > _quota.MaximumArtifactUtf8Bytes)
        {
            throw new IOException("The bounded credential-registry artifact limit would be exceeded.");
        }
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
        if (mutation.Kind == CredentialRegistryMutationKind.Tombstone)
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
        if (mutation is null || !Enum.IsDefined(mutation.Kind) || mutation.ExpectedRegistryRevision < 0 || mutation.OperationId is null || mutation.ReferenceId is null)
        {
            return false;
        }

        if (mutation.Kind == CredentialRegistryMutationKind.Register)
        {
            if (mutation.Reference is null || mutation.Binding is null || mutation.ConsentReference is null || mutation.Health is null || mutation.ProviderLocator is null || !Enum.IsDefined(mutation.Health.Value) || !mutation.Reference.Id.Equals(mutation.ReferenceId) || !mutation.Binding.ReferenceId.Equals(mutation.ReferenceId) || !string.Equals(mutation.Reference.ProviderId.Value, mutation.Binding.Implementation.ProviderId.Value, StringComparison.Ordinal) || !CredentialContractJson.TrySerialize(mutation.Reference, out _, out _) || !CredentialContractJson.TrySerialize(mutation.Binding, out _, out _))
            {
                return false;
            }
        }
        else if (mutation.Kind == CredentialRegistryMutationKind.SetHealth)
        {
            if (mutation.Health is null || mutation.Reference is not null || mutation.Binding is not null || mutation.ConsentReference is not null || mutation.ProviderLocator is not null || !Enum.IsDefined(mutation.Health.Value))
            {
                return false;
            }
        }
        else if (mutation.Reference is not null || mutation.Binding is not null || mutation.ConsentReference is not null || mutation.Health is not null || mutation.ProviderLocator is not null)
        {
            return false;
        }

        requestHash = Hash(JsonSerializer.Serialize(mutation, _canonicalJson));
        return true;
    }

    private bool ValidatePair(CredentialRegistryDocument publicDocument, CredentialRegistryPrivateDocument privateDocument, string identity)
    {
        if (publicDocument.SchemaVersion != CredentialRegistryDocument.CurrentSchemaVersion || privateDocument.SchemaVersion != CredentialRegistryPrivateDocument.CurrentSchemaVersion || publicDocument.Revision < 0 || privateDocument.Revision != publicDocument.Revision || !string.Equals(publicDocument.WorkspaceIdentity, identity, StringComparison.Ordinal) || !string.Equals(privateDocument.WorkspaceIdentity, identity, StringComparison.Ordinal) || string.IsNullOrEmpty(publicDocument.AuthenticationTag) || _utf8.GetByteCount(publicDocument.AuthenticationTag) > _trustProvider.MaximumAuthenticationTagUtf8Bytes || publicDocument.Entries is null || publicDocument.Tombstones is null || publicDocument.Operations is null || publicDocument.Evidence is null || privateDocument.Locators is null || publicDocument.Entries.Count > _quota.MaximumEntries || publicDocument.Tombstones.Count > _quota.MaximumTombstones || publicDocument.Operations.Count > _quota.MaximumOperations || publicDocument.Evidence.Count > _quota.MaximumEvidence)
        {
            return false;
        }

        var expected = StateDigest(publicDocument with { StateDigest = string.Empty, ContentDigest = string.Empty, AuthenticationTag = string.Empty }, privateDocument with { StateDigest = string.Empty });
        if (!FixedTimeEquals(publicDocument.StateDigest, expected) || !FixedTimeEquals(privateDocument.StateDigest, expected))
        {
            return false;
        }

        var entryIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in publicDocument.Entries)
        {
            if (!TryMapEntry(entry, out var mapped) || mapped!.Revision > publicDocument.Revision || !entryIds.Add(mapped.Reference.Id.Value) || !privateDocument.Locators.Any(locator => string.Equals(locator.ReferenceId, mapped.Reference.Id.Value, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        if (!publicDocument.Entries.Select(GetReferenceId).SequenceEqual(publicDocument.Entries.Select(GetReferenceId).Order(StringComparer.Ordinal), StringComparer.Ordinal) || !privateDocument.Locators.Select(item => item.ReferenceId).SequenceEqual(privateDocument.Locators.Select(item => item.ReferenceId).Order(StringComparer.Ordinal), StringComparer.Ordinal))
        {
            return false;
        }

        var locatorIds = new HashSet<string>(StringComparer.Ordinal);
        if (privateDocument.Locators.Any(item => !locatorIds.Add(item.ReferenceId) || !entryIds.Contains(item.ReferenceId) || !CredentialProviderLocator.TryParse(item.Locator, out _)))
        {
            return false;
        }

        var tombstoneIds = new HashSet<string>(StringComparer.Ordinal);
        if (publicDocument.Tombstones.Any(item => !CredentialReferenceId.TryParse(item.ReferenceId, out _, out _) || item.Revision < 1 || item.Revision > publicDocument.Revision || !CredentialContractId.TryParse(item.OperationId, out _, out _) || !CredentialContractHash.TryParse(item.ReferenceHash, out _, out _) || item.TombstonedAtUtc.Offset != TimeSpan.Zero || !tombstoneIds.Add(item.ReferenceId) || entryIds.Contains(item.ReferenceId)))
        {
            return false;
        }

        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        if (publicDocument.Operations.Any(item => !CredentialContractId.TryParse(item.OperationId, out _, out _) || !CredentialContractHash.TryParse(item.RequestHash, out _, out _) || item.Revision < 1 || item.Revision > publicDocument.Revision || !operationIds.Add(item.OperationId) || !CredentialReferenceId.TryParse(item.ReferenceId, out _, out _) || item.ResultEntry is not null && !TryMapEntry(item.ResultEntry, out _)))
        {
            return false;
        }

        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        return publicDocument.Evidence.All(item => CredentialContractJson.TryDeserializeEvidence(item.EvidenceJson, out var evidence, out _) && evidenceIds.Add(evidence!.EvidenceId.Value));
    }

    private static bool TryMapEntry(CredentialRegistryEntryDocument document, out CredentialRegistryEntry? entry)
    {
        entry = null;
        if (document is null || document.Revision < 1 || !CredentialContractJson.TryDeserializeReference(document.ReferenceJson, out var reference, out _) || !CredentialContractJson.TryDeserializeBinding(document.BindingJson, out var binding, out _) || !CredentialContractJson.TryHash(binding, out var bindingHash, out _) || !bindingHash!.Value.Equals(document.BindingHash, StringComparison.Ordinal) || !CredentialContractId.TryParse(document.ConsentReference, out var consent, out _) || !CredentialContractId.TryParse(document.LastOperationId, out var operation, out _) || !Enum.IsDefined((CredentialProviderHealthStatus)document.Health) || !reference!.Id.Equals(binding!.ReferenceId))
        {
            return false;
        }

        entry = new CredentialRegistryEntry(reference, binding, bindingHash, consent!, (CredentialProviderHealthStatus)document.Health, document.Revision, operation!);
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
        var tombstones = state.Public.Tombstones.Select(item => new CredentialRegistryTombstone(ParseReferenceId(item.ReferenceId), item.Revision, ParseContractId(item.OperationId), item.TombstonedAtUtc, ParseHash(item.ReferenceHash))).ToArray();
        var operations = state.Public.Operations.Select(item => new CredentialRegistryOperationEvidence(ParseContractId(item.OperationId), ParseHash(item.RequestHash), item.Kind, item.Revision, ParseReferenceId(item.ReferenceId))).ToArray();
        var evidence = state.Public.Evidence.Select(item => CredentialContractJson.TryDeserializeEvidence(item.EvidenceJson, out var mapped, out _) ? mapped! : throw new FormatException()).ToArray();
        return new CredentialRegistryReadResult(state.Public.Revision, entries, tombstones, operations, evidence, null);
    }

    private static State Empty(string identity) => Complete(identity, new CredentialRegistryDocument(1, identity, 0, 0, [], [], [], [], string.Empty, string.Empty, string.Empty), new CredentialRegistryPrivateDocument(1, identity, 0, [], string.Empty));

    private static State Complete(string identity, CredentialRegistryDocument publicDocument, CredentialRegistryPrivateDocument privateDocument)
    {
        var digest = StateDigest(publicDocument with { WorkspaceIdentity = identity, StateDigest = string.Empty, ContentDigest = string.Empty, AuthenticationTag = string.Empty }, privateDocument with { WorkspaceIdentity = identity, StateDigest = string.Empty });
        return new State(identity, publicDocument with { WorkspaceIdentity = identity, StateDigest = digest, ContentDigest = string.Empty, AuthenticationTag = string.Empty }, privateDocument with { WorkspaceIdentity = identity, StateDigest = digest }, false);
    }

    private static string StateDigest(CredentialRegistryDocument publicDocument, CredentialRegistryPrivateDocument privateDocument) => Hash(JsonSerializer.Serialize(publicDocument, _canonicalJson) + "\n" + JsonSerializer.Serialize(privateDocument, _canonicalJson));
    private static string Hash(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedTimeEquals(string? left, string right) => left is not null && left.Length == right.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));
    private static string WorkspaceIdentity(string material) => Hash("embodysense-credential-registry-workspace-v1\n" + material);
    private static bool MatchesCurrent(CredentialRegistryDocument document, CapabilityCatalogTrustState trust) => document.Generation == trust.CurrentGeneration && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);
    private static bool MatchesPrevious(CredentialRegistryDocument document, CapabilityCatalogTrustState trust) => trust.PreviousGeneration == document.Generation && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);
    private static bool Matches(CredentialRegistryEntryDocument document, CredentialReferenceId id) => string.Equals(GetReferenceId(document), id.Value, StringComparison.Ordinal);
    private static string GetReferenceId(CredentialRegistryEntryDocument document) => CredentialContractJson.TryDeserializeReference(document.ReferenceJson, out var reference, out _) ? reference!.Id.Value : throw new FormatException();
    private static bool HasEvidenceId(CredentialRegistryEvidenceDocument document, CredentialContractId id) => CredentialContractJson.TryDeserializeEvidence(document.EvidenceJson, out var evidence, out _) && evidence!.EvidenceId.Equals(id);
    private static CredentialReferenceId ParseReferenceId(string value) => CredentialReferenceId.TryParse(value, out var parsed, out _) ? parsed! : throw new FormatException();
    private static CredentialContractId ParseContractId(string value) => CredentialContractId.TryParse(value, out var parsed, out _) ? parsed! : throw new FormatException();
    private static CredentialContractHash ParseHash(string value) => CredentialContractHash.TryParse(value, out var parsed, out _) ? parsed! : throw new FormatException();
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
