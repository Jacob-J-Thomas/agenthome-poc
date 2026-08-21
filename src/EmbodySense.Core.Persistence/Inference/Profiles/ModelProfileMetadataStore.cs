using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Inference.Profiles.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles;

/// <summary>Provides bounded server-owned, authenticated, append-only model-profile metadata revisions.</summary>
/// <remarks>
/// This source stores only safe public metadata and hashes. Configuration changes require an exact current source
/// revision and a strictly advancing configuration revision; profile lifecycle remains exclusively in the shared
/// capability catalog. The configured server-state root must not be a governed workspace or workspace descendant.
/// </remarks>
public sealed class ModelProfileMetadataStore : IModelProfileMetadataSource
{
    private const int MaximumIdentifierCharacters = 128;
    private readonly ModelProfileMetadataStoreOptions _options;
    private readonly ModelProfileMetadataStorePaths _paths;
    private readonly AuthenticatedModelPersistenceStore<ModelProfileMetadataStoreDocument> _store;

    /// <summary>Creates a metadata source beneath an explicit server-owned state root.</summary>
    public ModelProfileMetadataStore(string serverStateRootPath, ModelProfileMetadataStoreOptions? options = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
        : this(serverStateRootPath, new FileCapabilityCatalogTrustProvider(new ModelProfileMetadataStorePaths(serverStateRootPath).TrustRootPath), options, durabilityBarrier)
    {
    }

    /// <summary>Creates a metadata source with an explicit server-owned trust provider.</summary>
    public ModelProfileMetadataStore(string serverStateRootPath, ICapabilityCatalogTrustProvider trustProvider, ModelProfileMetadataStoreOptions? options = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverStateRootPath);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _options = ValidateOptions(options ?? new ModelProfileMetadataStoreOptions());
        _paths = new ModelProfileMetadataStorePaths(serverStateRootPath);
        _store = new AuthenticatedModelPersistenceStore<ModelProfileMetadataStoreDocument>(
            _paths.RootPath,
            _paths.PrimaryPath,
            _paths.ProofPath,
            _paths.LockPath,
            "embodysense-model-profile-metadata-store-v1",
            _options.MaxArtifactUtf8Bytes,
            trustProvider,
            durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance,
            _options.PathObserver,
            EmptyDocument,
            ValidateDocument,
            IsDirectSuccessor);
    }

    /// <inheritdoc />
    public async Task<ModelProfileSourceReadResult> ReadAsync(CapabilityId profileId, CancellationToken cancellationToken = default)
    {
        if (!IsCanonicalProfileId(profileId))
        {
            return ReadResult(ModelProfileSourceReadStatus.Unavailable);
        }

        try
        {
            await using var session = await _store.AcquireForReadAsync(cancellationToken);
            if (session is null)
            {
                return ReadResult(ModelProfileSourceReadStatus.NotFound);
            }
            var loaded = await _store.LoadAsync(session, cancellationToken);
            var document = loaded.Disposition switch
            {
                AuthenticatedModelPersistenceDisposition.Current => loaded.Document,
                AuthenticatedModelPersistenceDisposition.Pending => loaded.Pending,
                _ => null
            };
            if (document is null)
            {
                return ReadResult(ModelProfileSourceReadStatus.Unavailable);
            }
            var revision = FindCurrent(document, profileId.Value);
            if (revision is null)
            {
                return ReadResult(ModelProfileSourceReadStatus.NotFound);
            }
            return CopyRead(revision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return ReadResult(ModelProfileSourceReadStatus.Unavailable);
        }
    }

    /// <summary>Publishes one exact safe metadata revision through operation-id replay and source-revision compare/exchange.</summary>
    /// <param name="operationId">A canonical idempotent server-owned publication operation.</param>
    /// <param name="metadata">The complete safe metadata revision.</param>
    /// <param name="expectedSourceRevisionHash">The exact current source revision for an update, or null for initial creation.</param>
    /// <param name="cancellationToken">Cancels work before an ambiguous durable boundary.</param>
    public async Task<ModelProfileMetadataPublishResult> PublishAsync(string operationId, GovernedModelProfileMetadata metadata, string? expectedSourceRevisionHash, CancellationToken cancellationToken = default)
    {
        if (!CustomLoopArtifactIdentifier.IsValid(operationId, MaximumIdentifierCharacters)
            || !GovernedModelContractValidator.IsValid(metadata)
            || expectedSourceRevisionHash is not null && !IsHash(expectedSourceRevisionHash))
        {
            return PublishResult(ModelProfileMetadataPublishStatus.Conflict);
        }

        var mayHavePublished = false;
        ModelProfileMetadataPublishResult? callbackResult = null;
        try
        {
            await using var session = await _store.AcquireForMutationAsync(cancellationToken);
            var loaded = await _store.LoadAsync(session, cancellationToken);
            if (loaded.Disposition == AuthenticatedModelPersistenceDisposition.Pending)
            {
                var pendingOperation = loaded.Pending!.Revisions[^1];
                if (string.Equals(pendingOperation.OperationId, operationId, StringComparison.Ordinal))
                {
                    mayHavePublished = true;
                }
                _ = await _store.FinalizePendingAsync(loaded, cancellationToken);
                loaded = await _store.LoadAsync(session, cancellationToken);
            }
            if (loaded.Disposition is AuthenticatedModelPersistenceDisposition.Recovered or AuthenticatedModelPersistenceDisposition.Unavailable || loaded.Document is null)
            {
                return PublishResult(ModelProfileMetadataPublishStatus.Unavailable);
            }

            var current = loaded.Document;
            var priorOperation = current.Revisions.SingleOrDefault(revision => string.Equals(revision.OperationId, operationId, StringComparison.Ordinal));
            if (priorOperation is not null)
            {
                callbackResult = SamePublication(priorOperation, metadata, expectedSourceRevisionHash)
                    ? PublishResult(ModelProfileMetadataPublishStatus.AlreadyPresent, PublishedSourceRevisionHash(priorOperation))
                    : PublishResult(ModelProfileMetadataPublishStatus.Conflict, FindCurrent(current, priorOperation.ProfileId)?.SourceRevisionHash);
                return callbackResult;
            }

            var profileId = metadata.DescriptorIdentity.Id.Value;
            var prior = FindCurrent(current, profileId);
            if (prior is not null && string.Equals(prior.Metadata.ContentHash, metadata.ContentHash, StringComparison.Ordinal))
            {
                if (expectedSourceRevisionHash is not null && !FixedTimeHashEquals(prior.SourceRevisionHash, expectedSourceRevisionHash))
                {
                    return PublishResult(ModelProfileMetadataPublishStatus.Conflict, prior.SourceRevisionHash);
                }
                if (current.Revisions.Count >= _options.MaxRevisions)
                {
                    return PublishResult(ModelProfileMetadataPublishStatus.Unavailable, prior.SourceRevisionHash);
                }
                var receipt = CreateReceipt(operationId, metadata, expectedSourceRevisionHash, prior);
                var receiptCandidate = CreateCandidate(current, receipt);
                if (!ValidateDocument(receiptCandidate, current.WorkspaceIdentity) || !IsDirectSuccessor(current, receiptCandidate))
                {
                    return PublishResult(ModelProfileMetadataPublishStatus.Unavailable, prior.SourceRevisionHash);
                }
                mayHavePublished = true;
                _ = await _store.CommitAsync(session, loaded, receiptCandidate, ObserveAsync, cancellationToken);
                callbackResult = PublishResult(ModelProfileMetadataPublishStatus.AlreadyPresent, prior.SourceRevisionHash);
                return callbackResult;
            }
            if (prior is null ? expectedSourceRevisionHash is not null : !FixedTimeHashEquals(prior.SourceRevisionHash, expectedSourceRevisionHash)
                || prior is not null && metadata.ConfigurationRevision <= prior.Metadata.ConfigurationRevision)
            {
                return PublishResult(ModelProfileMetadataPublishStatus.Conflict, prior?.SourceRevisionHash);
            }
            if (current.Revisions.Count >= _options.MaxRevisions || prior is null && current.CurrentProfiles.Count >= _options.MaxProfiles)
            {
                return PublishResult(ModelProfileMetadataPublishStatus.Unavailable, prior?.SourceRevisionHash);
            }

            var revision = CreateRevision(operationId, metadata, prior);
            var candidate = CreateCandidate(current, revision);
            if (!ValidateDocument(candidate, current.WorkspaceIdentity) || !IsDirectSuccessor(current, candidate))
            {
                return PublishResult(ModelProfileMetadataPublishStatus.Unavailable, prior?.SourceRevisionHash);
            }
            mayHavePublished = true;
            _ = await _store.CommitAsync(session, loaded, candidate, ObserveAsync, cancellationToken);
            callbackResult = PublishResult(ModelProfileMetadataPublishStatus.Published, revision.SourceRevisionHash);
            return callbackResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && !mayHavePublished)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception) || exception is OperationCanceledException)
        {
            return callbackResult ?? await AuthenticatePublicationAsync(operationId, metadata, expectedSourceRevisionHash).ConfigureAwait(false);
        }
    }

    private async Task<ModelProfileMetadataPublishResult> AuthenticatePublicationAsync(string operationId, GovernedModelProfileMetadata metadata, string? expectedSourceRevisionHash)
    {
        try
        {
            await using var session = await _store.AcquireForReadAsync(CancellationToken.None);
            if (session is null)
            {
                return PublishResult(ModelProfileMetadataPublishStatus.Unavailable);
            }
            var loaded = await _store.LoadAsync(session, CancellationToken.None);
            var operation = loaded.Disposition == AuthenticatedModelPersistenceDisposition.Current
                ? loaded.Document?.Revisions.SingleOrDefault(revision => string.Equals(revision.OperationId, operationId, StringComparison.Ordinal))
                : loaded.Disposition == AuthenticatedModelPersistenceDisposition.Pending
                    ? loaded.Pending?.Revisions.SingleOrDefault(revision => string.Equals(revision.OperationId, operationId, StringComparison.Ordinal))
                    : null;
            return operation is not null && SamePublication(operation, metadata, expectedSourceRevisionHash)
                ? PublishResult(ModelProfileMetadataPublishStatus.Published, PublishedSourceRevisionHash(operation))
                : PublishResult(ModelProfileMetadataPublishStatus.Unavailable);
        }
        catch
        {
            return PublishResult(ModelProfileMetadataPublishStatus.Unavailable);
        }
    }

    private static ModelProfileMetadataRevision CreateRevision(string operationId, GovernedModelProfileMetadata metadata, ModelProfileMetadataRevision? prior)
    {
        var generation = checked((prior?.ProfileGeneration ?? 0) + 1);
        var sourceRevision = ModelProfileMetadataSourceRevisionHash.Compute(metadata.DescriptorIdentity.Id.Value, generation, metadata.ContentHash, prior?.SourceRevisionHash, operationId);
        return new ModelProfileMetadataRevision(1, metadata.DescriptorIdentity.Id.Value, generation, operationId, prior?.SourceRevisionHash, metadata, prior?.SourceRevisionHash, sourceRevision);
    }

    private static ModelProfileMetadataRevision CreateReceipt(string operationId, GovernedModelProfileMetadata metadata, string? expectedSourceRevisionHash, ModelProfileMetadataRevision prior)
    {
        var receiptRevisionHash = ModelProfileMetadataOperationReceiptHash.Compute(prior.ProfileId, prior.ProfileGeneration, metadata.ContentHash, expectedSourceRevisionHash, prior.SourceRevisionHash, operationId);
        return new(1, prior.ProfileId, prior.ProfileGeneration, operationId, expectedSourceRevisionHash, metadata, prior.SourceRevisionHash, receiptRevisionHash, AdvancesProfile: false);
    }

    private static ModelProfileMetadataStoreDocument CreateCandidate(ModelProfileMetadataStoreDocument current, ModelProfileMetadataRevision revision)
    {
        var revisions = current.Revisions.Append(revision).ToArray();
        return new ModelProfileMetadataStoreDocument(1, current.WorkspaceIdentity, checked(current.Generation + 1), revisions, BuildPointers(revisions), string.Empty, string.Empty);
    }

    private static IReadOnlyList<ModelProfileMetadataCurrentPointer> BuildPointers(IReadOnlyList<ModelProfileMetadataRevision> revisions)
        => revisions.Where(revision => revision.AdvancesProfile).GroupBy(revision => revision.ProfileId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(revision => revision.ProfileGeneration).Last())
            .OrderBy(revision => revision.ProfileId, StringComparer.Ordinal)
            .Select(revision => new ModelProfileMetadataCurrentPointer(revision.ProfileId, revision.ProfileGeneration, revision.SourceRevisionHash))
            .ToArray();

    private bool ValidateDocument(ModelProfileMetadataStoreDocument document, string workspaceIdentity)
    {
        try
        {
            if (document.SchemaVersion != 1 || !string.Equals(document.WorkspaceIdentity, workspaceIdentity, StringComparison.Ordinal)
                || document.Generation < 0 || document.Revisions is null || document.CurrentProfiles is null
                || document.Generation != document.Revisions.Count
                || document.Revisions.Count > _options.MaxRevisions
                || document.CurrentProfiles.Count > _options.MaxProfiles)
            {
                return false;
            }
            var operations = new HashSet<string>(StringComparer.Ordinal);
            var profileState = new Dictionary<string, ModelProfileMetadataRevision>(StringComparer.Ordinal);
            foreach (var revision in document.Revisions)
            {
                if (revision is null || revision.SchemaVersion != 1 || !CustomLoopArtifactIdentifier.IsValid(revision.OperationId, MaximumIdentifierCharacters)
                    || !GovernedModelContractValidator.IsValid(revision.Metadata)
                    || !string.Equals(revision.ProfileId, revision.Metadata.DescriptorIdentity.Id.Value, StringComparison.Ordinal)
                    || !operations.Add(revision.OperationId))
                {
                    return false;
                }
                profileState.TryGetValue(revision.ProfileId, out var prior);
                if (revision.AdvancesProfile)
                {
                    if (revision.ProfileGeneration != (prior?.ProfileGeneration ?? 0) + 1
                        || !string.Equals(revision.PreviousSourceRevisionHash, prior?.SourceRevisionHash, StringComparison.Ordinal)
                        || !string.Equals(revision.ExpectedSourceRevisionHash, prior?.SourceRevisionHash, StringComparison.Ordinal)
                        || prior is not null && (revision.Metadata.ConfigurationRevision <= prior.Metadata.ConfigurationRevision || string.Equals(revision.Metadata.ContentHash, prior.Metadata.ContentHash, StringComparison.Ordinal))
                        || !string.Equals(revision.SourceRevisionHash, ModelProfileMetadataSourceRevisionHash.Compute(revision.ProfileId, revision.ProfileGeneration, revision.Metadata.ContentHash, revision.PreviousSourceRevisionHash, revision.OperationId), StringComparison.Ordinal))
                    {
                        return false;
                    }
                    profileState[revision.ProfileId] = revision;
                }
                else if (prior is null
                    || revision.ProfileGeneration != prior.ProfileGeneration
                    || !string.Equals(revision.Metadata.ContentHash, prior.Metadata.ContentHash, StringComparison.Ordinal)
                    || !string.Equals(revision.PreviousSourceRevisionHash, prior.SourceRevisionHash, StringComparison.Ordinal)
                    || revision.ExpectedSourceRevisionHash is not null && !string.Equals(revision.ExpectedSourceRevisionHash, prior.SourceRevisionHash, StringComparison.Ordinal)
                    || !string.Equals(revision.SourceRevisionHash, ModelProfileMetadataOperationReceiptHash.Compute(revision.ProfileId, revision.ProfileGeneration, revision.Metadata.ContentHash, revision.ExpectedSourceRevisionHash, revision.PreviousSourceRevisionHash!, revision.OperationId), StringComparison.Ordinal))
                {
                    return false;
                }
            }
            var expectedPointers = BuildPointers(document.Revisions);
            return document.CurrentProfiles.SequenceEqual(expectedPointers);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDirectSuccessor(ModelProfileMetadataStoreDocument current, ModelProfileMetadataStoreDocument candidate)
        => candidate.Generation == current.Generation + 1
            && candidate.SchemaVersion == current.SchemaVersion
            && string.Equals(candidate.WorkspaceIdentity, current.WorkspaceIdentity, StringComparison.Ordinal)
            && candidate.Revisions.Count == current.Revisions.Count + 1
            && candidate.Revisions.Take(current.Revisions.Count).Zip(current.Revisions).All(pair => string.Equals(pair.First.SourceRevisionHash, pair.Second.SourceRevisionHash, StringComparison.Ordinal));

    private static ModelProfileMetadataStoreDocument EmptyDocument(string workspaceIdentity)
        => new(1, workspaceIdentity, 0, [], [], string.Empty, string.Empty);

    private static ModelProfileMetadataRevision? FindCurrent(ModelProfileMetadataStoreDocument document, string profileId)
    {
        var pointer = document.CurrentProfiles.SingleOrDefault(value => string.Equals(value.ProfileId, profileId, StringComparison.Ordinal));
        return pointer is null ? null : document.Revisions.Single(revision => string.Equals(revision.SourceRevisionHash, pointer.SourceRevisionHash, StringComparison.Ordinal));
    }

    private static ModelProfileSourceReadResult CopyRead(ModelProfileMetadataRevision revision)
    {
        if (!GovernedModelContractJson.TrySerializeProfileMetadata(revision.Metadata, out var json, out _)
            || !GovernedModelContractJson.TryDeserializeProfileMetadata(json, out var copy, out _))
        {
            return ReadResult(ModelProfileSourceReadStatus.Unavailable);
        }
        return new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.Found, copy, revision.SourceRevisionHash);
    }

    private static bool SamePublication(ModelProfileMetadataRevision revision, GovernedModelProfileMetadata metadata, string? expectedSourceRevisionHash)
        => string.Equals(revision.Metadata.ContentHash, metadata.ContentHash, StringComparison.Ordinal)
            && string.Equals(revision.ExpectedSourceRevisionHash, expectedSourceRevisionHash, StringComparison.Ordinal);

    private static string PublishedSourceRevisionHash(ModelProfileMetadataRevision revision)
        => revision.AdvancesProfile ? revision.SourceRevisionHash : revision.PreviousSourceRevisionHash!;

    private static bool IsCanonicalProfileId(CapabilityId? profileId)
        => profileId is not null && CapabilityId.TryParse(profileId.Value, out var parsed, out _) && profileId.Equals(parsed);

    private static bool IsHash(string? value)
        => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool FixedTimeHashEquals(string left, string? right)
        => IsHash(left) && IsHash(right) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right!));

    private static ModelProfileMetadataStoreOptions ValidateOptions(ModelProfileMetadataStoreOptions options)
    {
        if (options.MaxProfiles is < 1 or > ModelProfileMetadataStoreOptions.MaximumProfiles
            || options.MaxRevisions is < 1 or > ModelProfileMetadataStoreOptions.MaximumRevisions
            || options.MaxArtifactUtf8Bytes is < 1 or > ModelProfileMetadataStoreOptions.MaximumArtifactUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Model-profile metadata source options must remain within schema-1 bounds.");
        }
        return options;
    }

    private ValueTask ObserveAsync(AuthenticatedModelPersistenceCommitStage stage, CancellationToken cancellationToken)
        => _options.DurableBoundaryObserver is null
            ? ValueTask.CompletedTask
            : _options.DurableBoundaryObserver((ModelProfileMetadataPersistenceBoundary)(int)stage, cancellationToken);

    private static ModelProfileSourceReadResult ReadResult(ModelProfileSourceReadStatus status) => new(status, null, null);

    private static ModelProfileMetadataPublishResult PublishResult(ModelProfileMetadataPublishStatus status, string? revision = null) => new(status, revision);

    private static bool IsAvailabilityFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException or CryptographicException or InvalidOperationException or AuthenticatedModelPersistenceLimitException;
}
