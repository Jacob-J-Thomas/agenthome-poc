using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Governance.Authority;
using EmbodySense.Core.Application.Governance.Authority.Models;
using EmbodySense.Core.Common.Authority;
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
public sealed class AuthorityProfileStore : IAuthorityProfileStore
{
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(false);
    private static readonly UTF8Encoding _strictUtf8 = new(false, true);
    private readonly WorkspacePaths _paths;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a store with the default server-owned proof provider.</summary>
    /// <param name="paths">The workspace paths that bound authority artifacts.</param>
    /// <param name="timeProvider">The optional trusted store clock.</param>
    /// <param name="durabilityBarrier">The optional post-rename durability boundary.</param>
    public AuthorityProfileStore(WorkspacePaths paths, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null) : this(paths, FileCapabilityCatalogTrustProvider.CreateDefault(), timeProvider, durabilityBarrier)
    {
    }

    /// <summary>Creates a store over an explicit server-owned proof provider.</summary>
    /// <param name="paths">The workspace paths that bound authority artifacts.</param>
    /// <param name="trustProvider">The server-owned provider that authenticates workspace state.</param>
    /// <param name="timeProvider">The optional trusted store clock.</param>
    /// <param name="durabilityBarrier">The optional post-rename durability boundary.</param>
    public AuthorityProfileStore(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _paths = paths;
        _pathGuard = new CapabilityCatalogPathGuard(paths.RootPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
        _trustProvider = trustProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
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
            await using var session = await AcquireLockAsync(cancellationToken);
            var identity = CreateWorkspaceIdentity(session.PhysicalIdentityMaterial);
            var trust = await _trustProvider.ReadAsync(identity, cancellationToken);
            var loaded = await LoadAsync(session, identity, trust, cancellationToken);
            if (loaded.Document is null)
            {
                return new AuthorityProfileReadResult(AuthorityProfileReadStatus.Unavailable, null, "No trustworthy authority-profile state is available.");
            }

            var profile = loaded.Document.Profiles.SingleOrDefault(value => string.Equals(value.ProfileId, id!.Value, StringComparison.Ordinal));
            if (profile is null)
            {
                return new AuthorityProfileReadResult(AuthorityProfileReadStatus.NotFound, null, "The authority profile does not exist.");
            }

            return new AuthorityProfileReadResult(loaded.Recovered ? AuthorityProfileReadStatus.RecoveredLastProved : AuthorityProfileReadStatus.Available, MapRecord(loaded.Document, profile), loaded.Recovered ? "The primary authority profile artifact was unsafe; the last proved state is read-only." : "The current authority profile is available.");
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

            var transition = ApplyTransition(current, mutation);
            if (transition.Status != AuthorityProfileMutationStatus.Applied)
            {
                return Result(transition.Status, mutation.OperationId, transition.Profile is null ? null : MapRecord(current, transition.Profile), transition.Detail);
            }

            var profile = transition.Profile!;
            var operation = new AuthorityProfileOperationDocument(mutation.OperationId, requestHash, mutation.Kind, AuthorityProfileMutationStatus.Applied, profile.ProfileId, transition.ResultingRevision, mutation.ActorId.Value, mutation.Reason.Value, _timeProvider.GetUtcNow());
            var profiles = current.Profiles.Where(value => !string.Equals(value.ProfileId, profile.ProfileId, StringComparison.Ordinal)).Append(profile).OrderBy(value => value.ProfileId, StringComparer.Ordinal).ToArray();
            var candidate = new AuthorityProfileStoreDocument(AuthorityProfileStoreDocument.CurrentSchemaVersion, identity, checked(current.Generation + 1), profiles, current.Operations.Append(operation).OrderBy(value => value.OperationId, StringComparer.Ordinal).ToArray(), string.Empty, string.Empty);
            await CommitAsync(session, identity, current, candidate, trust, cancellationToken);
            return Result(AuthorityProfileMutationStatus.Applied, mutation.OperationId, MapRecord(candidate, profile), transition.Detail);
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

    private Transition ApplyTransition(AuthorityProfileStoreDocument current, AuthorityProfileMutation mutation)
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

            var revision = NewRevision(mutation.Profile!, mutation.OperationId);
            return new Transition(AuthorityProfileMutationStatus.Applied, new AuthorityProfileDocument(targetId, [revision], null), revision.Revision, "The non-self-granting profile declaration was retained.");
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
            var tombstone = new AuthorityProfileTombstoneDocument(mutation.OperationId, mutation.ActorId.Value, mutation.Reason.Value, _timeProvider.GetUtcNow());
            return new Transition(AuthorityProfileMutationStatus.Applied, existing with { Tombstone = tombstone }, latest.Revision, "The profile tombstone was retained without rewriting profile history.");
        }

        if (existing.Revisions.Count >= AuthorityProfileStoreLimits.MaximumRevisionsPerProfile)
        {
            return new Transition(AuthorityProfileMutationStatus.Unavailable, existing, null, "The immutable profile revision quota is exhausted; no revision or receipt was written.");
        }

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

        var appended = NewRevision(successor, mutation.OperationId);
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

        if (proof is not null && (MatchesCurrent(proof, trust) || MatchesPrevious(proof, trust)))
        {
            return new LoadResult(proof, true);
        }

        return primary is not null && MatchesPrevious(primary, trust) ? new LoadResult(primary, true) : new LoadResult(null, false);
    }

    private async Task<AuthorityProfileStoreDocument?> TryReadAsync(CapabilityCatalogPathSession session, string identity, string path, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await session.ReadAllBytesAsync(path, AuthorityProfileStoreLimits.MaximumArtifactUtf8Bytes, cancellationToken);
            var document = JsonSerializer.Deserialize<AuthorityProfileStoreDocument>(_strictUtf8.GetString(bytes), _jsonOptions);
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
        if (document.SchemaVersion != AuthorityProfileStoreDocument.CurrentSchemaVersion || !string.Equals(document.WorkspaceIdentity, identity, StringComparison.Ordinal) || document.Generation < 0 || document.Profiles is null || document.Operations is null || document.Profiles.Count > AuthorityProfileStoreLimits.MaximumProfiles || document.Operations.Count > AuthorityProfileStoreLimits.MaximumOperationReceipts || !CapabilityIntegrityDigest.TryParse(document.ContentDigest, out var supplied, out _) || !supplied!.FixedTimeEquals(ComputeContentDigest(document)))
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
            if (!IsOperationIdValid(operation.OperationId) || !operationIds.Add(operation.OperationId) || !CapabilityIntegrityDigest.TryParse(operation.RequestHash, out _, out _) || operation.Outcome != AuthorityProfileMutationStatus.Applied || !Enum.IsDefined(operation.Kind) || !AuthorityProfileId.TryParse(operation.ProfileId, out _, out _) || !AuthorityActorId.TryParse(operation.ActorId, out _, out _) || !AuthorityPurpose.TryParse(operation.Reason, out _, out _) || operation.RecordedAtUtc.Offset != TimeSpan.Zero)
            {
                return false;
            }
        }

        return document.Profiles.Select(value => value.ProfileId).SequenceEqual(document.Profiles.Select(value => value.ProfileId).Order(StringComparer.Ordinal), StringComparer.Ordinal) && document.Operations.Select(value => value.OperationId).SequenceEqual(document.Operations.Select(value => value.OperationId).Order(StringComparer.Ordinal), StringComparer.Ordinal);
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
            if (revision.Revision != expected++ || !IsOperationIdValid(revision.OperationId) || revision.RecordedAtUtc.Offset != TimeSpan.Zero || !AuthorityProfileJson.TryDeserialize(revision.ProfileJson, out var profile, out _) || !string.Equals(profile!.ProfileId.Value, id!.Value, StringComparison.Ordinal) || profile.Revision.Value != revision.Revision || !AuthorityProfileHash.TryCompute(profile, out var hash, out _) || !string.Equals(hash!.Value, revision.ProfileHash, StringComparison.Ordinal))
            {
                return false;
            }

            revisions.Add(new AuthorityProfileRevisionEvidence(profile, hash!, revision.OperationId, revision.RecordedAtUtc));
        }

        record = new AuthorityProfileRecord(id!, revisions[^1].Profile, revisions[^1].Hash, revisions, document.Tombstone is null ? null : MapTombstone(document.Tombstone), []);
        return true;
    }

    private static AuthorityProfileRecord MapRecord(AuthorityProfileStoreDocument document, AuthorityProfileDocument profile, AuthorityProfileOperationDocument? receipt = null)
    {
        if (!TryMapProfile(profile, out var mapped))
        {
            throw new FormatException("The authority-profile record is invalid.");
        }

        var limit = receipt?.ResultingRevision ?? int.MaxValue;
        var revisions = mapped!.Revisions.Where(value => value.Profile.Revision.Value <= limit).ToArray();
        var operations = document.Operations.Where(value => string.Equals(value.ProfileId, mapped.ProfileId.Value, StringComparison.Ordinal) && (receipt is null || receipt.Kind == AuthorityProfileMutationKind.Tombstone || value.ResultingRevision <= receipt.ResultingRevision)).Select(MapReceipt).ToArray();
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
        if (!IsOperationIdValid(document.OperationId) || document.RecordedAtUtc.Offset != TimeSpan.Zero || !AuthorityActorId.TryParse(document.ActorId, out var actorId, out _) || !AuthorityPurpose.TryParse(document.Reason, out var reason, out _))
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

    private AuthorityProfileRevisionDocument NewRevision(AuthorityProfile profile, string operationId)
    {
        _ = AuthorityProfileJson.TrySerialize(profile, out var json, out _);
        _ = AuthorityProfileHash.TryCompute(profile, out var hash, out _);
        return new AuthorityProfileRevisionDocument(profile.Revision.Value, json!, hash!.Value, operationId, _timeProvider.GetUtcNow());
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
        var empty = new AuthorityProfileStoreDocument(AuthorityProfileStoreDocument.CurrentSchemaVersion, identity, 0, [], [], string.Empty, string.Empty);
        return empty with { ContentDigest = ComputeContentDigest(empty).Value };
    }

    private static CapabilityIntegrityDigest ComputeContentDigest(AuthorityProfileStoreDocument document) => CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _hashOptions)));

    private static string ComputeRequestHash(AuthorityProfileMutation mutation)
    {
        var profileJson = mutation.Profile is null ? string.Empty : AuthorityProfileJson.TrySerialize(mutation.Profile, out var json, out _) ? json! : string.Empty;
        var content = $"{(int)mutation.Kind}\n{mutation.OperationId}\n{mutation.ExpectedRevision}\n{mutation.ProfileId?.Value ?? mutation.Profile?.ProfileId.Value}\n{(int?)mutation.Status}\n{profileJson}\n{mutation.ActorId.Value}\n{mutation.Reason.Value}";
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value;
    }

    private static string CreateWorkspaceIdentity(string physicalIdentity) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("embodysense-authority-profile-workspace-physical-v1\n" + physicalIdentity))).ToLowerInvariant();

    private static bool IsOperationIdValid(string? value) => !string.IsNullOrEmpty(value) && value.Length <= AuthorityProfileStoreLimits.MaximumOperationIdCharacters && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.');
    private static bool MatchesCurrent(AuthorityProfileStoreDocument document, CapabilityCatalogTrustState trust) => document.Generation == trust.CurrentGeneration && string.Equals(document.ContentDigest, trust.CurrentContentDigest, StringComparison.Ordinal);
    private static bool MatchesPrevious(AuthorityProfileStoreDocument document, CapabilityCatalogTrustState trust) => trust.PreviousGeneration == document.Generation && string.Equals(document.ContentDigest, trust.PreviousContentDigest, StringComparison.Ordinal);
    private static AuthorityProfileMutationResult Result(AuthorityProfileMutationStatus status, string operationId, AuthorityProfileRecord? record, string detail) => new(status, operationId, record, detail);
    private static bool IsAvailabilityFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or FormatException or JsonException or OverflowException;
    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented) => new(JsonSerializerDefaults.Web) { WriteIndented = writeIndented, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, false) } };

    private sealed record LoadResult(AuthorityProfileStoreDocument? Document, bool Recovered);
    private sealed record SerializedDocument(string Json, string ContentDigest);
    private sealed record Transition(AuthorityProfileMutationStatus Status, AuthorityProfileDocument? Profile, int? ResultingRevision, string Detail);
}
