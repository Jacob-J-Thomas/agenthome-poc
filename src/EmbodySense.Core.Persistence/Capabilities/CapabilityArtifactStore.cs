using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Persists immutable capability payloads and a schema-1 atomic activation index.</summary>
/// <remarks>Activation state is deliberately separate from declaration, installation, enablement, health, and trust lifecycle axes in the catalog.</remarks>
public sealed class CapabilityArtifactStore : ICapabilityArtifactStore
{
    private const int MaximumActivationBytes = 1_048_576;
    private const int MaximumOperations = 256;
    private const int MaximumEntries = 256;
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
    private static readonly JsonDocumentOptions _jsonDocumentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };
    private readonly WorkspacePaths _paths;
    private readonly CapabilityCatalogPathGuard _guard;
    private readonly TimeProvider _timeProvider;
    private readonly ICapabilityArtifactStateTrustProvider _trustProvider;
    private readonly ICapabilityArtifactTrustVerifier _artifactTrustVerifier;

    /// <summary>Creates a workspace-local artifact store.</summary>
    public CapabilityArtifactStore(WorkspacePaths paths, ICapabilityArtifactStateTrustProvider trustProvider, ICapabilityArtifactTrustVerifier artifactTrustVerifier, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(artifactTrustVerifier);
        _paths = paths;
        _trustProvider = trustProvider;
        _artifactTrustVerifier = artifactTrustVerifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _guard = new CapabilityCatalogPathGuard(paths.CapabilityCatalogPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactStoreResult> StageAsync(CapabilityArtifactStageRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CapabilityArtifactManifestValidator.Validate(request.Manifest).IsValid)
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "Only structurally valid artifacts may be staged.");
        }

        var bytes = request.Content.ToArray();
        var digest = CapabilityIntegrityDigest.Compute(bytes);
        if (!request.Manifest.Checksum.FixedTimeEquals(digest))
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "Staged bytes do not match the verified manifest checksum.");
        }

        var trust = await _artifactTrustVerifier.VerifyAsync(request.Manifest, digest, cancellationToken);
        if (trust.Status != CapabilityArtifactTrustStatus.Verified)
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "The exact staged artifact was not verified by server-owned trust policy.");
        }
        request = request with { Trust = trust };

        try
        {
            await using var fileSystem = await AcquireAsync(createRoot: true, cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var digestName = request.Manifest.Checksum.Value["sha256:".Length..];
            var artifactRoot = Path.Combine(_paths.CapabilityArtifactsPath, "staged", digestName);
            var contentPath = Path.Combine(artifactRoot, request.Manifest.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            var evidencePath = Path.Combine(artifactRoot, "artifact.evidence.json");
            fileSystem.PrepareDirectory(Path.GetDirectoryName(contentPath)!);
            if (fileSystem.FileExists(contentPath))
            {
                var existing = await fileSystem.ReadAllBytesAsync(contentPath, CapabilityArtifactManifestValidator.MaximumArtifactBytes, cancellationToken);
                if (!request.Manifest.Checksum.FixedTimeEquals(CapabilityIntegrityDigest.Compute(existing)))
                {
                    return Result(CapabilityArtifactStoreStatus.Invalid, null, "Immutable staged content conflicts with the verified digest.");
                }
            }
            else
            {
                await fileSystem.WriteBytesAtomicallyAsync(contentPath, bytes, cancellationToken);
            }

            var evidence = await SerializeEvidenceAsync(workspaceIdentity, request, cancellationToken);
            if (fileSystem.FileExists(evidencePath))
            {
                var existing = Encoding.UTF8.GetString(await fileSystem.ReadAllBytesAsync(evidencePath, MaximumActivationBytes, cancellationToken));
                if (!string.Equals(existing, evidence, StringComparison.Ordinal) || !await IsStagedAsync(fileSystem, workspaceIdentity, request.Manifest.Checksum.Value, request.Manifest.Descriptor.Id.Value, request.Manifest.Descriptor.Version.Value, cancellationToken))
                {
                    return Result(CapabilityArtifactStoreStatus.Invalid, null, "Immutable staged evidence conflicts with the verified manifest.");
                }
                return Result(CapabilityArtifactStoreStatus.NoChange, null, "The exact verified artifact is already staged.");
            }

            await fileSystem.WriteTextAtomicallyAsync(evidencePath, evidence, cancellationToken);
            return Result(CapabilityArtifactStoreStatus.Applied, null, "The verified artifact was staged immutably.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Immutable artifact staging is unavailable; activation state was not changed.");
        }
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactStoreResult> ActivateAsync(CapabilityArtifactActivationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CapabilityArtifactManifestValidator.Validate(request.Manifest).IsValid || !CapabilityArtifactManifestValidator.IsOperationId(request.OperationId) || request.ExpectedRevision < 0)
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "The activation request is invalid.");
        }

        try
        {
            await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var state = await LoadProvedForMutationAsync(fileSystem, cancellationToken);
            if (state is null)
            {
                return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Activation requires one proved current state.");
            }

            var replay = state.Operations.SingleOrDefault(operation => string.Equals(operation.OperationId, request.OperationId, StringComparison.Ordinal));
            if (replay is not null)
            {
                var exact = replay.Kind == "activate" && replay.CapabilityId == request.Manifest.Descriptor.Id.Value && replay.RequestDigest == CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(request.Manifest).Value && replay.ArtifactDigest == request.Manifest.Checksum.Value && replay.ExpectedRevision == request.ExpectedRevision;
                var replayEntry = state.Entries.SingleOrDefault(entry => entry.CapabilityId == replay.CapabilityId && entry.Revision == replay.ResultRevision);
                return exact ? Result(CapabilityArtifactStoreStatus.Replayed, replayEntry is null ? null : Map(replayEntry), "The exact activation operation was replayed.") : Result(CapabilityArtifactStoreStatus.Invalid, null, "The operation identity is already bound to different activation evidence.");
            }

            if (state.Revision != request.ExpectedRevision)
            {
                return Result(CapabilityArtifactStoreStatus.Conflict, Current(state, request.Manifest.Descriptor.Id.Value), "The activation revision changed before this operation.");
            }
            if (state.Operations.Count >= MaximumOperations)
            {
                return Result(CapabilityArtifactStoreStatus.Unavailable, Current(state, request.Manifest.Descriptor.Id.Value), "The durable activation idempotency ledger is full and refuses a new operation.");
            }

            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            if (!await IsStagedAsync(fileSystem, workspaceIdentity, request.Manifest.Checksum.Value, request.Manifest.Descriptor.Id.Value, request.Manifest.Descriptor.Version.Value, cancellationToken))
            {
                return Result(CapabilityArtifactStoreStatus.NotFound, Current(state, request.Manifest.Descriptor.Id.Value), "The verified immutable artifact is not staged.");
            }

            var revision = checked(state.Revision + 1);
            var existing = state.Entries.SingleOrDefault(entry => entry.CapabilityId == request.Manifest.Descriptor.Id.Value);
            var entry = new CapabilityArtifactActivationEntryDocument(request.Manifest.Descriptor.Id.Value, request.Manifest.Checksum.Value, existing?.ArtifactDigest, revision, _timeProvider.GetUtcNow().ToUniversalTime());
            var entries = state.Entries.Where(item => item.CapabilityId != entry.CapabilityId).Append(entry).OrderBy(item => item.CapabilityId, StringComparer.Ordinal).ToArray();
            if (entries.Length > MaximumEntries)
            {
                return Result(CapabilityArtifactStoreStatus.Unavailable, Current(state, entry.CapabilityId), "The bounded activation catalog is full.");
            }

            var operation = new CapabilityArtifactOperationDocument(request.OperationId, "activate", entry.CapabilityId, CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(request.Manifest).Value, entry.ArtifactDigest, request.ExpectedRevision, revision);
            var operations = state.Operations.Append(operation).ToArray();
            var candidate = Seal(new CapabilityArtifactActivationDocument(CapabilityArtifactActivationDocument.CurrentSchemaVersion, revision, entries, operations, string.Empty, string.Empty));
            await CommitAsync(fileSystem, state, candidate, cancellationToken);
            return Result(CapabilityArtifactStoreStatus.Applied, Map(entry), "The verified artifact was atomically activated without changing catalog lifecycle axes.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Atomic activation is unavailable; the last proved activation was preserved.");
        }
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactStoreResult> RollbackAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        if (expectedRevision < 0 || !CapabilityArtifactManifestValidator.IsOperationId(operationId))
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "The rollback request is invalid.");
        }

        try
        {
            await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var state = await LoadProvedForMutationAsync(fileSystem, cancellationToken);
            if (state is null)
            {
                return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Rollback requires one proved current state.");
            }

            var replay = state.Operations.SingleOrDefault(operation => operation.OperationId == operationId);
            if (replay is not null)
            {
                var exact = replay.Kind == "rollback" && replay.CapabilityId == capabilityId.Value && replay.RequestDigest == capabilityId.Value + "@" + expectedRevision && replay.ExpectedRevision == expectedRevision;
                return exact ? Result(CapabilityArtifactStoreStatus.Replayed, Current(state, capabilityId.Value), "The exact rollback operation was replayed.") : Result(CapabilityArtifactStoreStatus.Invalid, null, "The operation identity is already bound to different rollback evidence.");
            }

            if (state.Revision != expectedRevision)
            {
                return Result(CapabilityArtifactStoreStatus.Conflict, Current(state, capabilityId.Value), "The activation revision changed before rollback.");
            }
            if (state.Operations.Count >= MaximumOperations)
            {
                return Result(CapabilityArtifactStoreStatus.Unavailable, Current(state, capabilityId.Value), "The durable activation idempotency ledger is full and refuses a new operation.");
            }

            var existing = state.Entries.SingleOrDefault(entry => entry.CapabilityId == capabilityId.Value);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            if (existing?.PriorArtifactDigest is null || !await IsStagedAsync(fileSystem, workspaceIdentity, existing.PriorArtifactDigest, capabilityId.Value, expectedVersion: null, cancellationToken))
            {
                return Result(CapabilityArtifactStoreStatus.NotFound, existing is null ? null : Map(existing), "No prior proved staged artifact is available for rollback.");
            }

            var revision = checked(state.Revision + 1);
            var rolledBack = existing with { ArtifactDigest = existing.PriorArtifactDigest, PriorArtifactDigest = existing.ArtifactDigest, Revision = revision, ActivatedAtUtc = _timeProvider.GetUtcNow().ToUniversalTime() };
            var entries = state.Entries.Where(entry => entry.CapabilityId != capabilityId.Value).Append(rolledBack).OrderBy(entry => entry.CapabilityId, StringComparer.Ordinal).ToArray();
            var operation = new CapabilityArtifactOperationDocument(operationId, "rollback", capabilityId.Value, capabilityId.Value + "@" + expectedRevision, rolledBack.ArtifactDigest, expectedRevision, revision);
            var candidate = Seal(new CapabilityArtifactActivationDocument(CapabilityArtifactActivationDocument.CurrentSchemaVersion, revision, entries, state.Operations.Append(operation).ToArray(), string.Empty, string.Empty));
            await CommitAsync(fileSystem, state, candidate, cancellationToken);
            return Result(CapabilityArtifactStoreStatus.Applied, Map(rolledBack), "The immediately prior proved artifact was atomically restored.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Atomic rollback is unavailable; the last proved activation was preserved.");
        }
    }

    /// <inheritdoc />
    public async Task<CapabilityArtifactStoreResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        try
        {
            await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var state = await LoadAsync(fileSystem, cancellationToken);
            if (state is null)
            {
                var artifactsExist = fileSystem.FileExists(_paths.CapabilityArtifactActivationPath) || fileSystem.FileExists(_paths.CapabilityArtifactActivationProofPath);
                return artifactsExist ? Result(CapabilityArtifactStoreStatus.Unavailable, null, "Artifact activation state exists but cannot be proved by server-owned trust.") : Result(CapabilityArtifactStoreStatus.NotFound, null, "No artifact activation state exists.");
            }

            var activation = Current(state, capabilityId.Value);
            return activation is null ? Result(CapabilityArtifactStoreStatus.NotFound, null, "The capability has no active artifact.") : Result(CapabilityArtifactStoreStatus.Applied, activation, "The current proved artifact activation is available.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Artifact activation state is unavailable.");
        }
    }

    /// <inheritdoc />
    public async Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CapabilityArtifactManifestValidator.Validate(invocation.Manifest).IsValid || invocation.ExpectedActivationRevision < 1)
        {
            return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Incompatible, null, "The executable activation request is invalid.");
        }
        CapabilityCatalogPathSession? fileSystem = null;
        try
        {
            fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var state = await LoadProvedForMutationAsync(fileSystem, cancellationToken);
            var activation = state?.Entries.SingleOrDefault(entry => entry.CapabilityId == invocation.Manifest.Descriptor.Id.Value);
            if (state is null || activation is null || state.Revision != invocation.ExpectedActivationRevision || activation.ArtifactDigest != invocation.Manifest.Checksum.Value)
            {
                return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The requested artifact is not the current proved activation.");
            }
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var expectedPin = CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(invocation.Manifest).Value;
            var root = Path.Combine(_paths.CapabilityArtifactsPath, "staged", activation.ArtifactDigest["sha256:".Length..]);
            var evidencePath = Path.Combine(root, "artifact.evidence.json");
            var evidenceBytes = await fileSystem.ReadAllBytesAsync(evidencePath, MaximumActivationBytes, cancellationToken);
            var evidence = DeserializeStrict<CapabilityArtifactEvidenceDocument>(evidenceBytes);
            if (evidence is null || evidence.ManifestPolicyPin != expectedPin || !await IsStagedAsync(fileSystem, workspaceIdentity, activation.ArtifactDigest, activation.CapabilityId, invocation.Manifest.Descriptor.Version.Value, cancellationToken))
            {
                return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The requested artifact evidence is not current and proved.");
            }
            var executablePath = Path.Combine(root, evidence.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            var executable = fileSystem.OpenBoundReadLease(executablePath, CapabilityArtifactManifestValidator.MaximumArtifactBytes);
            try
            {
                var bytes = new byte[checked((int)executable.Length)];
                await executable.ReadExactlyAsync(bytes, cancellationToken);
                executable.Position = 0;
                var actualDigest = CapabilityIntegrityDigest.Compute(bytes);
                if (!invocation.Manifest.Checksum.FixedTimeEquals(actualDigest))
                {
                    executable.Dispose();
                    return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The retained executable identity does not match the proved artifact digest.");
                }
                var lease = new CapabilityExecutableArtifactLease(fileSystem, executable, root, executablePath, actualDigest, state!.Revision);
                fileSystem = null;
                return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Available, lease, "The current proved immutable artifact is retained for isolated execution.");
            }
            catch
            {
                executable.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The immutable artifact activation cannot be resolved safely.");
        }
        finally
        {
            if (fileSystem is not null)
            {
                await fileSystem.DisposeAsync();
            }
        }
    }

    private async Task<CapabilityCatalogPathSession> AcquireAsync(bool createRoot, CancellationToken cancellationToken)
    {
        var session = await _guard.TryAcquireExclusiveSessionAsync(_paths.CapabilityArtifactLockPath, createRoot, cancellationToken) ?? throw new IOException("The capability artifact root is unavailable.");
        session.PrepareDirectory(_paths.CapabilityArtifactsPath);
        session.PrepareDirectory(Path.Combine(_paths.CapabilityArtifactsPath, "staged"));
        return session;
    }

    private async Task<CapabilityArtifactActivationDocument?> LoadProvedForMutationAsync(CapabilityCatalogPathSession fileSystem, CancellationToken cancellationToken)
    {
        var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadActivationAsync(workspaceIdentity, cancellationToken);
        var primary = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityArtifactActivationPath, cancellationToken);
        var proof = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityArtifactActivationProofPath, cancellationToken);
        if (primary is null && proof is null)
        {
            var empty = Seal(new CapabilityArtifactActivationDocument(CapabilityArtifactActivationDocument.CurrentSchemaVersion, 0, [], [], string.Empty, string.Empty));
            return trust is null || MatchesCurrent(empty, trust) ? empty : null;
        }
        return primary is not null && MatchesCurrent(primary, trust) ? primary : null;
    }

    private async Task<CapabilityArtifactActivationDocument?> LoadAsync(CapabilityCatalogPathSession fileSystem, CancellationToken cancellationToken)
    {
        var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadActivationAsync(workspaceIdentity, cancellationToken);
        var primary = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityArtifactActivationPath, cancellationToken);
        var proof = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityArtifactActivationProofPath, cancellationToken);
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return primary;
        }
        if (proof is not null && (MatchesCurrent(proof, trust) || MatchesPrevious(proof, trust)))
        {
            return proof;
        }
        return primary is not null && MatchesPrevious(primary, trust) ? primary : null;
    }

    private async Task<CapabilityArtifactActivationDocument?> ReadDocumentAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, string path, CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(path))
        {
            return null;
        }
        var bytes = await fileSystem.ReadAllBytesAsync(path, MaximumActivationBytes, cancellationToken);
        CapabilityArtifactActivationDocument? document;
        try
        {
            document = DeserializeStrict<CapabilityArtifactActivationDocument>(bytes);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return null;
        }
        if (document is null || !IsValid(document) || !await _trustProvider.VerifyActivationAsync(workspaceIdentity, document.Revision, document.ContentDigest, document.AuthenticationTag, cancellationToken))
        {
            return null;
        }
        return document;
    }

    private async Task CommitAsync(CapabilityCatalogPathSession fileSystem, CapabilityArtifactActivationDocument current, CapabilityArtifactActivationDocument candidate, CancellationToken cancellationToken)
    {
        var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
        var trust = await _trustProvider.ReadActivationAsync(workspaceIdentity, cancellationToken) ?? await _trustProvider.InitializeActivationAsync(workspaceIdentity, current.ContentDigest, cancellationToken);
        if (!MatchesCurrent(current, trust))
        {
            throw new IOException("The server-owned artifact activation anchor no longer matches the mutation base.");
        }
        var currentJson = await SerializeActivationAsync(workspaceIdentity, current, cancellationToken);
        var candidateJson = await SerializeActivationAsync(workspaceIdentity, candidate, cancellationToken);
        if (Encoding.UTF8.GetByteCount(currentJson) > MaximumActivationBytes || Encoding.UTF8.GetByteCount(candidateJson) > MaximumActivationBytes)
        {
            throw new IOException("Artifact activation state exceeds its bounded size.");
        }
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityArtifactActivationProofPath, currentJson, cancellationToken);
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityArtifactActivationPath, candidateJson, cancellationToken);
        _ = await _trustProvider.AdvanceActivationAsync(workspaceIdentity, trust.CurrentRevision, trust.CurrentContentDigest, candidate.Revision, candidate.ContentDigest, cancellationToken);
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityArtifactActivationProofPath, candidateJson, cancellationToken);
    }

    private async Task<bool> IsStagedAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, string digest, string expectedCapabilityId, string? expectedVersion, CancellationToken cancellationToken)
    {
        var root = Path.Combine(fileSystem.Root, "artifacts", "staged", digest["sha256:".Length..]);
        var evidencePath = Path.Combine(root, "artifact.evidence.json");
        if (!fileSystem.DirectoryExists(root) || !fileSystem.FileExists(evidencePath))
        {
            return false;
        }
        try
        {
            var bytes = await fileSystem.ReadAllBytesAsync(evidencePath, MaximumActivationBytes, cancellationToken);
            var evidence = DeserializeStrict<CapabilityArtifactEvidenceDocument>(bytes);
            if (evidence is null || evidence.SchemaVersion != 1 || evidence.Checksum != digest || evidence.CapabilityId != expectedCapabilityId || expectedVersion is not null && evidence.CapabilityVersion != expectedVersion || evidence.TrustStatus != CapabilityArtifactTrustStatus.Verified.ToString())
            {
                return false;
            }
            var expectedEvidenceDigest = ComputeEvidenceDigest(evidence);
            if (expectedEvidenceDigest != evidence.ContentDigest || !await _trustProvider.VerifyStagedEvidenceAsync(workspaceIdentity, digest, evidence.ContentDigest, evidence.AuthenticationTag, cancellationToken))
            {
                return false;
            }
            var contentPath = Path.Combine(root, evidence.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
            if (!fileSystem.FileExists(contentPath))
            {
                return false;
            }
            var content = await fileSystem.ReadAllBytesAsync(contentPath, CapabilityArtifactManifestValidator.MaximumArtifactBytes, cancellationToken);
            return CapabilityIntegrityDigest.Compute(content).Value == digest;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static CapabilityArtifactActivationDocument Seal(CapabilityArtifactActivationDocument document)
    {
        var content = JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _jsonOptions);
        return document with { ContentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value };
    }

    private static bool IsValid(CapabilityArtifactActivationDocument document)
    {
        if (document.SchemaVersion != CapabilityArtifactActivationDocument.CurrentSchemaVersion || document.Revision < 0 || document.Entries.Count > MaximumEntries || document.Operations.Count > MaximumOperations || !string.Equals(Seal(document).ContentDigest, document.ContentDigest, StringComparison.Ordinal))
        {
            return false;
        }
        return document.Entries.Select(entry => entry.CapabilityId).Distinct(StringComparer.Ordinal).Count() == document.Entries.Count && document.Operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).Count() == document.Operations.Count;
    }

    private async Task<string> SerializeEvidenceAsync(string workspaceIdentity, CapabilityArtifactStageRequest request, CancellationToken cancellationToken)
    {
        var evidence = new CapabilityArtifactEvidenceDocument(1, request.Manifest.Descriptor.Id.Value, request.Manifest.Descriptor.Version.Value, request.Manifest.Descriptor.Implementation.ProviderId.Value, request.Manifest.Descriptor.Implementation.ImplementationId, request.Manifest.Source.Kind.ToString(), request.Manifest.Source.Uri, request.Manifest.Source.Revision, request.Manifest.Source.UpdatePolicy.ToString(), request.Manifest.Checksum.Value, request.Manifest.Signature, request.Manifest.Platform.ToString(), request.Manifest.EntryPoint, request.Manifest.Arguments, request.Trust.Status.ToString(), request.Trust.Verifier, CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(request.Manifest).Value, string.Empty, string.Empty);
        evidence = evidence with { ContentDigest = ComputeEvidenceDigest(evidence) };
        evidence = evidence with { AuthenticationTag = await _trustProvider.AuthenticateStagedEvidenceAsync(workspaceIdentity, evidence.Checksum, evidence.ContentDigest, cancellationToken) };
        return JsonSerializer.Serialize(evidence, _jsonOptions) + Environment.NewLine;
    }

    private async Task<string> SerializeActivationAsync(string workspaceIdentity, CapabilityArtifactActivationDocument document, CancellationToken cancellationToken)
    {
        var authenticated = document with { AuthenticationTag = await _trustProvider.AuthenticateActivationAsync(workspaceIdentity, document.Revision, document.ContentDigest, cancellationToken) };
        return JsonSerializer.Serialize(authenticated, _jsonOptions) + Environment.NewLine;
    }

    private static string ComputeEvidenceDigest(CapabilityArtifactEvidenceDocument evidence)
    {
        var content = JsonSerializer.Serialize(evidence with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _jsonOptions);
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value;
    }

    private static bool MatchesCurrent(CapabilityArtifactActivationDocument document, CapabilityArtifactTrustState? trust) => trust is not null && document.Revision == trust.CurrentRevision && document.ContentDigest == trust.CurrentContentDigest;

    private static bool MatchesPrevious(CapabilityArtifactActivationDocument document, CapabilityArtifactTrustState? trust) => trust?.PreviousRevision == document.Revision && trust.PreviousContentDigest == document.ContentDigest;

    private static CapabilityArtifactActivation? Current(CapabilityArtifactActivationDocument state, string capabilityId)
    {
        var entry = state.Entries.SingleOrDefault(candidate => candidate.CapabilityId == capabilityId);
        return entry is null ? null : Map(entry);
    }

    private static CapabilityArtifactActivation Map(CapabilityArtifactActivationEntryDocument entry)
    {
        _ = CapabilityId.TryParse(entry.CapabilityId, out var id, out _);
        _ = CapabilityIntegrityDigest.TryParse(entry.ArtifactDigest, out var digest, out _);
        CapabilityIntegrityDigest? prior = null;
        if (entry.PriorArtifactDigest is not null)
        {
            _ = CapabilityIntegrityDigest.TryParse(entry.PriorArtifactDigest, out prior, out _);
        }
        return new CapabilityArtifactActivation(id!, digest!, prior, entry.Revision, entry.ActivatedAtUtc);
    }

    private static CapabilityArtifactStoreResult Result(CapabilityArtifactStoreStatus status, CapabilityArtifactActivation? activation, string detail) => new(status, activation, detail);

    private static T? DeserializeStrict<T>(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes, _jsonDocumentOptions);
        RejectDuplicateMembers(document.RootElement);
        return JsonSerializer.Deserialize<T>(document.RootElement, _jsonOptions);
    }

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("Artifact persistence JSON contains duplicate object members.");
                }
                RejectDuplicateMembers(property.Value);
            }
            return;
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateMembers(item);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false, WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
}
