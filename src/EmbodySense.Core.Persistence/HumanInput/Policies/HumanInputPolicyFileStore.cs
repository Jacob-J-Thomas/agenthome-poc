using EmbodySense.Core.Application.HumanInput.Policies;
using EmbodySense.Core.Application.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Loops.HumanInput.Policies;
using EmbodySense.Core.Common.Loops.HumanInput.Policies.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;
using EmbodySense.Core.Persistence.Capabilities.Models;
using EmbodySense.Core.Persistence.HumanInput.Policies.Models;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

/// <summary>Persists and resolves bounded exact immutable Human Input policy revisions from one workspace-owned file source.</summary>
/// <remarks>
/// Every lookup names both policy and revision. The store never follows replacement records, selects a latest revision, or provides a timeout default.
/// A schema-1 exact publication intent is published before a policy artifact or its mutable catalog generation changes. POSIX retains a parent-directory
/// barrier for that order. Windows has no portable parent-directory barrier, so only an exact retry may repair the one narrowly proved artifact-visible,
/// intent-absent orphan state; reads and all divergent, multiple, or corrupt states remain unavailable.
/// </remarks>
public sealed class HumanInputPolicyFileStore : IHumanInputPolicySource
{
    private const int MaximumInterruptedTemporaryArtifacts = 1;
    private const int SupportingFileCount = 3;
    private readonly string _rootPath;
    private readonly string _generationPath;
    private readonly string _publicationIntentPath;
    private readonly string _lockPath;
    private readonly HumanInputPolicyFileStoreOptions _options;
    private readonly CapabilityCatalogPathGuard _pathGuard;
    private readonly HumanInputPolicyFileStoreCanonicalPublisher _publisher;

    /// <summary>Creates the canonical bounded Human Input policy source for one initialized workspace.</summary>
    /// <param name="paths">The workspace whose private <c>.agent</c> policy source will be used.</param>
    /// <param name="options">Optional finite artifact-count and byte limits.</param>
    public HumanInputPolicyFileStore(WorkspacePaths paths, HumanInputPolicyFileStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _options = Validate(options ?? new HumanInputPolicyFileStoreOptions());
        _rootPath = Path.Combine(paths.AgentPath, "human-input", "policies");
        _generationPath = Path.Combine(_rootPath, "generation");
        _publicationIntentPath = Path.Combine(_rootPath, "publication.intent");
        _lockPath = Path.Combine(_rootPath, "mutation.lock");
        _pathGuard = new CapabilityCatalogPathGuard(paths.AgentPath, NativeCapabilityCatalogDurabilityBarrier.Instance, _options.PathObserver);
        _publisher = new HumanInputPolicyFileStoreCanonicalPublisher(_options.PhysicalBoundaryObserver);
    }

    /// <inheritdoc />
    public async Task<HumanInputPolicySourceReadResult> ReadAsync(HumanInputPolicyReference reference, CancellationToken cancellationToken = default)
    {
        if (!IsReference(reference)) return Read(HumanInputPolicySourceReadStatus.Unavailable, null, 0);
        try
        {
            await using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            await RetireInterruptedTemporaryArtifactsAsync(session, cancellationToken).ConfigureAwait(false);
            if (session.FileExistsBound(_publicationIntentPath)) throw new FormatException("The Human Input policy source has an unfinished publication intent that requires its exact retry.");
            var generation = await ReadGenerationAsync(session, cancellationToken).ConfigureAwait(false);
            var path = PathFor(reference);
            var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaximumArtifactUtf8Bytes, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return Read(HumanInputPolicySourceReadStatus.NotFound, null, generation.StoreGeneration);
            var policy = HumanInputPolicyArtifactJson.Deserialize(bytes);
            return Equals(policy.Reference, reference) ? Read(HumanInputPolicySourceReadStatus.Ready, policy, generation.StoreGeneration) : Read(HumanInputPolicySourceReadStatus.Unavailable, null, generation.StoreGeneration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Read(HumanInputPolicySourceReadStatus.Unavailable, null, 0);
        }
    }

    /// <summary>Appends one exact immutable policy revision when the caller's source generation remains current.</summary>
    /// <param name="artifact">The complete hash-authenticated policy artifact.</param>
    /// <param name="expectedStoreGeneration">The exact source generation observed before writing.</param>
    /// <param name="cancellationToken">A token that cancels before an unproven durable write boundary; cancellation after the durable intent requires an exact retry.</param>
    /// <returns>A committed, exact-replay, conflict, invalid, or unavailable result.</returns>
    public async Task<HumanInputPolicyFileStoreWriteResult> CommitAsync(HumanInputPolicyArtifact artifact, long expectedStoreGeneration, CancellationToken cancellationToken = default)
    {
        if (expectedStoreGeneration < 0 || !HumanInputPolicyArtifactValidator.Validate(artifact).IsValid) return Write(HumanInputPolicyFileStoreWriteStatus.Invalid, 0);
        try
        {
            await using var lease = await EnterAsync(cancellationToken).ConfigureAwait(false);
            var session = lease.Session;
            await RetireInterruptedTemporaryArtifactsAsync(session, cancellationToken).ConfigureAwait(false);
            var recovered = await RecoverPublicationAsync(session, artifact, expectedStoreGeneration, cancellationToken).ConfigureAwait(false);
            if (recovered is not null) return recovered;
            recovered = await RecoverWindowsArtifactVisibleOrphanAsync(session, artifact, expectedStoreGeneration, cancellationToken).ConfigureAwait(false);
            if (recovered is not null) return recovered;
            var generation = await ReadGenerationAsync(session, cancellationToken).ConfigureAwait(false);
            var path = PathFor(artifact.Reference);
            var existingBytes = await session.TryReadAllBytesBoundAsync(path, _options.MaximumArtifactUtf8Bytes, cancellationToken).ConfigureAwait(false);
            if (existingBytes is not null)
            {
                var existing = HumanInputPolicyArtifactJson.Deserialize(existingBytes);
                return Equals(existing, artifact) ? Write(HumanInputPolicyFileStoreWriteStatus.Replayed, generation.StoreGeneration) : Write(HumanInputPolicyFileStoreWriteStatus.Invalid, generation.StoreGeneration);
            }
            if (generation.StoreGeneration != expectedStoreGeneration) return Write(HumanInputPolicyFileStoreWriteStatus.Conflict, generation.StoreGeneration);
            if (generation.StoreGeneration == long.MaxValue) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, generation.StoreGeneration);
            if (generation.Artifacts.Count >= _options.MaximumArtifacts) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, generation.StoreGeneration);
            var bytes = HumanInputPolicyArtifactJson.Serialize(artifact);
            if (bytes.Length > _options.MaximumArtifactUtf8Bytes) return Write(HumanInputPolicyFileStoreWriteStatus.Invalid, generation.StoreGeneration);
            var next = checked(generation.StoreGeneration + 1);
            var nextGeneration = AddArtifact(generation, artifact, next);
            await PublishAsync(session, new HumanInputPolicyFileStorePublicationIntent(generation.StoreGeneration, artifact), path, bytes, nextGeneration, cancellationToken).ConfigureAwait(false);
            return Write(HumanInputPolicyFileStoreWriteStatus.Committed, next);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);
        }
    }

    private async Task<HumanInputPolicyFileStoreLease> EnterAsync(CancellationToken cancellationToken)
    {
        var session = await _pathGuard.TryAcquireExclusiveSessionAsync(_lockPath, createRoot: true, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("The bounded Human Input policy source mutation lease could not be acquired through its no-follow workspace path.");
        return new HumanInputPolicyFileStoreLease(session);
    }

    private async Task<HumanInputPolicyFileStoreGeneration> ReadGenerationAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var artifacts = await ReadArtifactCatalogAsync(session, cancellationToken).ConfigureAwait(false);
        var stored = await ReadStoredGenerationAsync(session, cancellationToken).ConfigureAwait(false);
        if (!stored.Exists && artifacts.Count != 0) throw new FormatException("The Human Input policy generation is missing.");
        if (!stored.Exists) return new HumanInputPolicyFileStoreGeneration(0, []);
        if (!CatalogEquals(stored.Generation, artifacts)) throw new FormatException("The Human Input policy generation does not match the immutable artifact catalog.");
        return stored.Generation;
    }

    private async Task<HumanInputPolicyFileStoreWriteResult?> RecoverPublicationAsync(CapabilityCatalogPathSession session, HumanInputPolicyArtifact artifact, long expectedStoreGeneration, CancellationToken cancellationToken)
    {
        if (!session.FileExistsBound(_publicationIntentPath)) return null;

        var intent = await ReadPublicationIntentAsync(session, cancellationToken).ConfigureAwait(false);
        if (!Equals(intent.Artifact, artifact) || intent.ExpectedStoreGeneration != expectedStoreGeneration) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);

        var nextGeneration = checked(intent.ExpectedStoreGeneration + 1);
        var path = PathFor(intent.Artifact.Reference);
        var artifactBytes = await session.TryReadAllBytesBoundAsync(path, _options.MaximumArtifactUtf8Bytes, cancellationToken).ConfigureAwait(false);
        var artifacts = await ReadArtifactCatalogAsync(session, cancellationToken).ConfigureAwait(false);
        var stored = await ReadStoredGenerationAsync(session, cancellationToken).ConfigureAwait(false);
        var currentGeneration = stored.Exists ? stored.Generation : new HumanInputPolicyFileStoreGeneration(0, []);
        if (artifactBytes is not null)
        {
            var existing = HumanInputPolicyArtifactJson.Deserialize(artifactBytes);
            if (!Equals(existing, intent.Artifact) || artifacts.Count != nextGeneration) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);
            if (currentGeneration.StoreGeneration == intent.ExpectedStoreGeneration && IsExactAdvance(currentGeneration, artifacts, intent.Artifact))
            {
                await WriteGenerationAsync(session, new HumanInputPolicyFileStoreGeneration(nextGeneration, artifacts), cancellationToken).ConfigureAwait(false);
                await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.GenerationPublished, cancellationToken).ConfigureAwait(false);
            }
            else if (!stored.Exists || stored.Generation.StoreGeneration != nextGeneration || !CatalogEquals(stored.Generation, artifacts))
            {
                return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);
            }
        }
        else
        {
            if (currentGeneration.StoreGeneration != intent.ExpectedStoreGeneration || !CatalogEquals(currentGeneration, artifacts)) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);
            var bytes = HumanInputPolicyArtifactJson.Serialize(intent.Artifact);
            await WriteArtifactAsync(session, path, bytes, cancellationToken).ConfigureAwait(false);
            await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.ArtifactPublished, cancellationToken).ConfigureAwait(false);
            await WriteGenerationAsync(session, AddArtifact(currentGeneration, intent.Artifact, nextGeneration), cancellationToken).ConfigureAwait(false);
            await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.GenerationPublished, cancellationToken).ConfigureAwait(false);
        }

        await RetirePublicationIntentAsync(session, cancellationToken).ConfigureAwait(false);
        return Write(HumanInputPolicyFileStoreWriteStatus.Replayed, nextGeneration);
    }

    private async Task PublishAsync(CapabilityCatalogPathSession session, HumanInputPolicyFileStorePublicationIntent intent, string artifactPath, byte[] artifactBytes, HumanInputPolicyFileStoreGeneration nextGeneration, CancellationToken cancellationToken)
    {
        var intentBytes = HumanInputPolicyFileStorePublicationIntentJson.Serialize(intent);
        await WritePublicationIntentAsync(session, intentBytes, cancellationToken).ConfigureAwait(false);
        await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.IntentPublished, cancellationToken).ConfigureAwait(false);
        await WriteArtifactAsync(session, artifactPath, artifactBytes, cancellationToken).ConfigureAwait(false);
        await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.ArtifactPublished, cancellationToken).ConfigureAwait(false);
        await WriteGenerationAsync(session, nextGeneration, cancellationToken).ConfigureAwait(false);
        await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.GenerationPublished, cancellationToken).ConfigureAwait(false);
        await RetirePublicationIntentAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HumanInputPolicyFileStorePublicationIntent> ReadPublicationIntentAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var bytes = await session.ReadAllBytesAsync(_publicationIntentPath, _options.MaximumArtifactUtf8Bytes * 2, cancellationToken).ConfigureAwait(false);
        return HumanInputPolicyFileStorePublicationIntentJson.Deserialize(bytes);
    }

    private async Task<(bool Exists, HumanInputPolicyFileStoreGeneration Generation)> ReadStoredGenerationAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var maximumGenerationBytes = checked((_options.MaximumArtifacts * 256) + 128);
        var bytes = await session.TryReadAllBytesBoundAsync(_generationPath, maximumGenerationBytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return (false, new HumanInputPolicyFileStoreGeneration(0, []));

        return (true, HumanInputPolicyFileStoreGenerationJson.Deserialize(bytes));
    }

    private async Task WriteArtifactAsync(CapabilityCatalogPathSession session, string path, byte[] bytes, CancellationToken cancellationToken)
        => await _publisher.PublishAsync(session, _rootPath, Path.GetFileName(path), bytes, overwrite: false, HumanInputPolicyFileStorePublicationPart.PolicyArtifact, cancellationToken).ConfigureAwait(false);

    private async Task WriteGenerationAsync(CapabilityCatalogPathSession session, HumanInputPolicyFileStoreGeneration generation, CancellationToken cancellationToken)
        => await _publisher.PublishAsync(session, _rootPath, Path.GetFileName(_generationPath), HumanInputPolicyFileStoreGenerationJson.Serialize(generation), overwrite: true, HumanInputPolicyFileStorePublicationPart.Generation, cancellationToken).ConfigureAwait(false);

    private async Task WritePublicationIntentAsync(CapabilityCatalogPathSession session, byte[] bytes, CancellationToken cancellationToken)
        => await _publisher.PublishAsync(session, _rootPath, Path.GetFileName(_publicationIntentPath), bytes, overwrite: false, HumanInputPolicyFileStorePublicationPart.PublicationIntent, cancellationToken).ConfigureAwait(false);

    private async Task RetirePublicationIntentAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        await _publisher.RetireAsync(session, _rootPath, Path.GetFileName(_publicationIntentPath), HumanInputPolicyFileStorePublicationPart.PublicationIntent, cancellationToken).ConfigureAwait(false);
        await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.PublicationIntentRetired, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary boundary, CancellationToken cancellationToken)
    {
        _options.DurableBoundaryObserver?.Invoke(boundary);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task<HumanInputPolicyFileStoreWriteResult?> RecoverWindowsArtifactVisibleOrphanAsync(CapabilityCatalogPathSession session, HumanInputPolicyArtifact artifact, long expectedStoreGeneration, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || session.FileExistsBound(_publicationIntentPath)) return null;

        var path = PathFor(artifact.Reference);
        var bytes = await session.TryReadAllBytesBoundAsync(path, _options.MaximumArtifactUtf8Bytes, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;

        var stored = await ReadStoredGenerationAsync(session, cancellationToken).ConfigureAwait(false);
        var currentGeneration = stored.Exists ? stored.Generation : new HumanInputPolicyFileStoreGeneration(0, []);
        var artifacts = await ReadArtifactCatalogAsync(session, cancellationToken).ConfigureAwait(false);
        if (CatalogEquals(currentGeneration, artifacts)) return null;
        if (currentGeneration.StoreGeneration != expectedStoreGeneration || currentGeneration.StoreGeneration == long.MaxValue || !IsExactAdvance(currentGeneration, artifacts, artifact))
        {
            return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);
        }

        var existing = HumanInputPolicyArtifactJson.Deserialize(bytes);
        if (!Equals(existing, artifact)) return Write(HumanInputPolicyFileStoreWriteStatus.Unavailable, 0);

        var next = checked(currentGeneration.StoreGeneration + 1);
        await WriteGenerationAsync(session, new HumanInputPolicyFileStoreGeneration(next, artifacts), cancellationToken).ConfigureAwait(false);
        await ObserveBoundaryAsync(HumanInputPolicyFileStorePublicationBoundary.GenerationPublished, cancellationToken).ConfigureAwait(false);
        return Write(HumanInputPolicyFileStoreWriteStatus.Replayed, next);
    }

    private async Task RetireInterruptedTemporaryArtifactsAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        string? temporary = null;
        foreach (var name in EnumerateSourceFiles(session).Select(file => file.Name))
        {
            if (IsCanonicalSourceName(name)) continue;
            if (!TryParseInterruptedTemporaryName(name, out _)) throw new FormatException($"The Human Input policy source contains an unsupported artifact `{name}`.");
            if (temporary is not null) throw new FormatException("The Human Input policy source exceeds its interrupted-write recovery bound.");
            temporary = name;
        }

        if (temporary is not null)
        {
            await _publisher.RetireAsync(session, _rootPath, temporary, HumanInputPolicyFileStorePublicationPart.InterruptedTemporary, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsCanonicalSourceName(string name)
        => string.Equals(name, "generation", StringComparison.Ordinal)
            || string.Equals(name, "publication.intent", StringComparison.Ordinal)
            || string.Equals(name, "mutation.lock", StringComparison.Ordinal)
            || IsPolicyArtifactName(name);

    private static bool TryParseInterruptedTemporaryName(string name, out string destinationName)
    {
        const string TemporaryMarker = ".tmp-";
        destinationName = string.Empty;
        var separator = name.LastIndexOf(TemporaryMarker, StringComparison.Ordinal);
        if (separator < 1 || separator + TemporaryMarker.Length + 32 != name.Length) return false;
        var nonce = name.AsSpan(separator + TemporaryMarker.Length);
        foreach (var character in nonce)
        {
            if (character is < '0' or > '9' and < 'a' or > 'f') return false;
        }

        destinationName = name[..separator];
        return IsCanonicalSourceName(destinationName) && !string.Equals(destinationName, "mutation.lock", StringComparison.Ordinal);
    }

    private static bool IsPolicyArtifactName(string name)
    {
        const string ArtifactSuffix = ".json";
        if (!name.EndsWith(ArtifactSuffix, StringComparison.Ordinal)) return false;
        return HumanInputPolicyReference.TryParse(name[..^ArtifactSuffix.Length], out _);
    }

    private string PathFor(HumanInputPolicyReference reference) => Path.Combine(_rootPath, reference.PolicyId + "@" + reference.RevisionId + ".json");

    private IReadOnlyList<(string Name, long Length)> EnumerateSourceFiles(CapabilityCatalogPathSession session)
    {
        var maximumEntries = checked(_options.MaximumArtifacts + SupportingFileCount + MaximumInterruptedTemporaryArtifacts);
        var maximumBytes = checked(((long)_options.MaximumArtifactUtf8Bytes * (_options.MaximumArtifacts + 2)) + ((long)_options.MaximumArtifacts * 256) + 128);
        return session.EnumerateRegularFiles(_rootPath, maximumEntries, maximumBytes);
    }

    private async Task<IReadOnlyList<HumanInputPolicyFileStoreCatalogEntry>> ReadArtifactCatalogAsync(CapabilityCatalogPathSession session, CancellationToken cancellationToken)
    {
        var entries = new List<HumanInputPolicyFileStoreCatalogEntry>();
        foreach (var name in EnumerateSourceFiles(session).Select(file => file.Name).Where(IsPolicyArtifactName))
        {
            if (entries.Count >= _options.MaximumArtifacts) throw new FormatException("The Human Input policy source exceeds its immutable artifact bound.");
            var bytes = await session.ReadAllBytesAsync(Path.Combine(_rootPath, name), _options.MaximumArtifactUtf8Bytes, cancellationToken).ConfigureAwait(false);
            var artifact = HumanInputPolicyArtifactJson.Deserialize(bytes);
            if (!string.Equals(Path.GetFileName(PathFor(artifact.Reference)), name, StringComparison.Ordinal)) throw new FormatException("The Human Input policy artifact path does not prove its exact reference.");
            entries.Add(new HumanInputPolicyFileStoreCatalogEntry(artifact.Reference, artifact.ContentHash));
        }

        var sorted = entries.OrderBy(entry => entry.Reference.ToString(), StringComparer.Ordinal).ToArray();
        if (sorted.Select(entry => entry.Reference).Distinct().Count() != sorted.Length) throw new FormatException("The Human Input policy artifact catalog contains a duplicate exact reference.");
        return sorted;
    }

    private static HumanInputPolicyFileStoreGeneration AddArtifact(HumanInputPolicyFileStoreGeneration generation, HumanInputPolicyArtifact artifact, long nextGeneration)
    {
        var entry = new HumanInputPolicyFileStoreCatalogEntry(artifact.Reference, artifact.ContentHash);
        var artifacts = generation.Artifacts.Append(entry).OrderBy(candidate => candidate.Reference.ToString(), StringComparer.Ordinal).ToArray();
        return new HumanInputPolicyFileStoreGeneration(nextGeneration, artifacts);
    }

    private static bool CatalogEquals(HumanInputPolicyFileStoreGeneration generation, IReadOnlyList<HumanInputPolicyFileStoreCatalogEntry> artifacts)
        => generation.StoreGeneration == artifacts.Count && generation.Artifacts.SequenceEqual(artifacts);

    private static bool IsExactAdvance(HumanInputPolicyFileStoreGeneration generation, IReadOnlyList<HumanInputPolicyFileStoreCatalogEntry> artifacts, HumanInputPolicyArtifact artifact)
    {
        if (generation.StoreGeneration == long.MaxValue || artifacts.Count != generation.StoreGeneration + 1) return false;
        var expected = AddArtifact(generation, artifact, checked(generation.StoreGeneration + 1));
        return expected.Artifacts.SequenceEqual(artifacts);
    }

    private static bool IsReference(HumanInputPolicyReference? reference) => reference is not null && HumanInputPolicyReference.TryParse(reference.ToString(), out _);

    private static HumanInputPolicySourceReadResult Read(HumanInputPolicySourceReadStatus status, HumanInputPolicyArtifact? policy, long generation) => new(status, policy, generation);

    private static HumanInputPolicyFileStoreWriteResult Write(HumanInputPolicyFileStoreWriteStatus status, long generation) => new(status, generation);

    private static HumanInputPolicyFileStoreOptions Validate(HumanInputPolicyFileStoreOptions options)
    {
        if (options.MaximumArtifacts is < 1 or > 1_024 || options.MaximumArtifactUtf8Bytes is < 256 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(options));
        return options;
    }

}
