using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Persists immutable capability payloads and a schema-1 atomic activation index.</summary>
/// <remarks>Activation state is deliberately separate from declaration, installation, enablement, health, and trust lifecycle axes in the catalog.</remarks>
public sealed class CapabilityArtifactStore : ICapabilityArtifactStore, ICapabilityPackageDependencyManifestDiscovery, ICapabilityLifecycleArtifactEvidenceSource, ICapabilityLifecycleTargetResolver
{
    private const int MaximumActivationBytes = 1_048_576;
    private const int MaximumStagedArtifacts = 256;
    private const int MaximumStagedArtifactEntries = 64;
    private const int MaximumAggregateEvidenceBytes = 4 * 1_048_576;
    private const int MaximumAggregateContentBytes = 128 * 1_048_576;
    private const int MaximumOperations = 256;
    private const int MaximumEntries = 256;
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions();
    private static readonly JsonDocumentOptions _jsonDocumentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };
    private readonly WorkspacePaths _paths;
    private readonly CapabilityCatalogPathGuard _guard;
    private readonly TimeProvider _timeProvider;
    private readonly ICapabilityArtifactStateTrustProvider _trustProvider;
    private readonly ICapabilityArtifactTrustVerifier _artifactTrustVerifier;
    private readonly ICapabilityLifecycleMutationStore? _lifecycleStore;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates a workspace-local artifact store.</summary>
    /// <param name="paths">The exact initialized workspace paths.</param>
    /// <param name="trustProvider">The server-owned artifact-state trust provider.</param>
    /// <param name="artifactTrustVerifier">The server-owned artifact policy verifier.</param>
    /// <param name="timeProvider">The optional clock used for persisted evidence timestamps.</param>
    /// <param name="durabilityBarrier">The optional platform durability barrier.</param>
    /// <param name="lifecycleStore">The optional lifecycle state source used to restrict executable resolution.</param>
    /// <param name="authorityTransaction">The optional shared workspace authority transaction.</param>
    /// <param name="pathObserver">The optional server-owned bounded child-open observer.</param>
    public CapabilityArtifactStore(WorkspacePaths paths, ICapabilityArtifactStateTrustProvider trustProvider, ICapabilityArtifactTrustVerifier artifactTrustVerifier, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityLifecycleMutationStore? lifecycleStore = null, ICapabilityAuthorityTransaction? authorityTransaction = null, ICapabilityCatalogPathObserver? pathObserver = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(artifactTrustVerifier);
        _paths = paths;
        _trustProvider = trustProvider;
        _artifactTrustVerifier = artifactTrustVerifier;
        _lifecycleStore = lifecycleStore;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _guard = new CapabilityCatalogPathGuard(paths.CapabilityCatalogPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance, pathObserver);
    }

    /// <inheritdoc />
    public Task<CapabilityArtifactStoreResult> StageAsync(CapabilityArtifactStageRequest request, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => StageCoreAsync(request, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityArtifactStoreResult> StageCoreAsync(CapabilityArtifactStageRequest request, CancellationToken cancellationToken)
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
    public Task<CapabilityArtifactStoreResult> ActivateAsync(CapabilityArtifactActivationRequest request, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => ActivateCoreAsync(request, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityArtifactStoreResult> ActivateCoreAsync(CapabilityArtifactActivationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!CapabilityArtifactManifestValidator.Validate(request.Manifest).IsValid || !CapabilityArtifactManifestValidator.IsOperationId(request.OperationId) || request.ExpectedRevision < 0)
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "The activation request is invalid.");
        }
        var lifecycleBoundary = await ReadLifecycleBoundaryAsync(request.Manifest.Descriptor.Id, cancellationToken);
        if (lifecycleBoundary is { Status: CapabilityLifecycleReadStatus.Available, State: null })
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Current lifecycle registration state is incomplete; direct activation fails closed.");
        }
        if (lifecycleBoundary?.Status == CapabilityLifecycleReadStatus.Available)
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "Registered capability activation must use the dependent-aware lifecycle preview and mutation boundary.");
        }
        if (lifecycleBoundary?.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable)
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Current lifecycle registration state is unproved; direct activation fails closed.");
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
    public Task<CapabilityArtifactStoreResult> RollbackAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => RollbackCoreAsync(capabilityId, expectedRevision, operationId, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityArtifactStoreResult> RollbackCoreAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        if (expectedRevision < 0 || !CapabilityArtifactManifestValidator.IsOperationId(operationId))
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "The rollback request is invalid.");
        }
        var lifecycleBoundary = await ReadLifecycleBoundaryAsync(capabilityId, cancellationToken);
        if (lifecycleBoundary is { Status: CapabilityLifecycleReadStatus.Available, State: null })
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Current lifecycle registration state is incomplete; direct rollback fails closed.");
        }
        if (lifecycleBoundary?.Status == CapabilityLifecycleReadStatus.Available)
        {
            return Result(CapabilityArtifactStoreStatus.Invalid, null, "Registered capability rollback must use the dependent-aware lifecycle preview and mutation boundary.");
        }
        if (lifecycleBoundary?.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable)
        {
            return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Current lifecycle registration state is unproved; direct rollback fails closed.");
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
    public Task<CapabilityArtifactStoreResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => ReadCoreAsync(capabilityId, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityArtifactStoreResult> ReadCoreAsync(CapabilityId capabilityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        if (_lifecycleStore is not null)
        {
            var lifecycle = await _lifecycleStore.ReadAsync(capabilityId, cancellationToken);
            if (lifecycle.Status == CapabilityLifecycleReadStatus.Available)
            {
                if (lifecycle.State is not { } state)
                {
                    return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Capability lifecycle state is incomplete.");
                }
                if (state.IsRemoved)
                {
                    return Result(CapabilityArtifactStoreStatus.NotFound, null, "The capability lifecycle identity is tombstoned.");
                }
                var prior = lifecycle.History.LastOrDefault()?.ArtifactDigest;
                return Result(CapabilityArtifactStoreStatus.Applied, new CapabilityArtifactActivation(capabilityId, state.ArtifactDigest, prior, state.Revision, state.UpdatedAtUtc), "The current authenticated lifecycle artifact is available.");
            }
            if (lifecycle.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable)
            {
                return Result(CapabilityArtifactStoreStatus.Unavailable, null, "Capability lifecycle state is unavailable.");
            }
        }
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
    public Task<IReadOnlyList<CapabilityPackageDependencyDiscovery>> DiscoverAsync(CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(DiscoverCoreAsync, cancellationToken);

    private async Task<IReadOnlyList<CapabilityPackageDependencyDiscovery>> DiscoverCoreAsync(CancellationToken cancellationToken)
    {
        await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
        var state = await LoadProvedForMutationAsync(fileSystem, cancellationToken) ?? throw new IOException("Activated package dependencies require one proved activation state.");
        var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
        var discoveries = new List<CapabilityPackageDependencyDiscovery>();
        foreach (var activation in state.Entries)
        {
            var artifactDigest = activation.ArtifactDigest;
            if (_lifecycleStore is not null && CapabilityId.TryParse(activation.CapabilityId, out var capabilityId, out _))
            {
                var lifecycle = await _lifecycleStore.ReadAsync(capabilityId!, cancellationToken);
                if (lifecycle.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable)
                {
                    throw new IOException("Activated package lifecycle state is unavailable.");
                }
                if (lifecycle.Status == CapabilityLifecycleReadStatus.Available && lifecycle.State is null)
                {
                    throw new IOException("Activated package lifecycle state is incomplete.");
                }
                if (lifecycle.State is { IsRemoved: true })
                {
                    continue;
                }
                artifactDigest = lifecycle.State?.ArtifactDigest.Value ?? artifactDigest;
            }
            var root = Path.Combine(_paths.CapabilityArtifactsPath, "staged", artifactDigest["sha256:".Length..]);
            var evidenceBytes = await fileSystem.ReadAllBytesAsync(Path.Combine(root, "artifact.evidence.json"), MaximumActivationBytes, cancellationToken);
            var evidence = DeserializeStrict<CapabilityArtifactEvidenceDocument>(evidenceBytes);
            if (evidence is null || !await IsStagedAsync(fileSystem, workspaceIdentity, artifactDigest, activation.CapabilityId, evidence.CapabilityVersion, cancellationToken))
            {
                throw new FormatException("Activated package evidence is unavailable or unproved.");
            }
            if (evidence.Dependencies is { } dependencies)
            {
                if (!CapabilityDependencyManifestValidator.Validate(dependencies).IsValid || dependencies.Kind != CapabilityDependencyManifestKind.CapabilityPackage || dependencies.SubjectId.Value != activation.CapabilityId)
                {
                    throw new FormatException("Activated package dependency evidence is invalid or forged.");
                }
                discoveries.Add(new CapabilityPackageDependencyDiscovery(activation.CapabilityId, artifactDigest, dependencies));
            }
        }
        return discoveries;
    }

    /// <inheritdoc />
    public Task<CapabilityLifecycleArtifactEvidence> VerifyAsync(CapabilityDescriptor descriptor, CapabilityIntegrityDigest artifactDigest, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => VerifyCoreAsync(descriptor, artifactDigest, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecycleArtifactEvidence> VerifyCoreAsync(CapabilityDescriptor descriptor, CapabilityIntegrityDigest artifactDigest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(artifactDigest);
        if (!CapabilityDescriptorJson.TrySerialize(descriptor, out var descriptorJson, out _))
        {
            return new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "The lifecycle target descriptor is invalid.");
        }
        try
        {
            await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var root = Path.Combine(_paths.CapabilityArtifactsPath, "staged", artifactDigest.Value["sha256:".Length..]);
            var evidenceBytes = await fileSystem.ReadAllBytesAsync(Path.Combine(root, "artifact.evidence.json"), MaximumActivationBytes, cancellationToken);
            var evidence = DeserializeStrict<CapabilityArtifactEvidenceDocument>(evidenceBytes);
            var proved = evidence is not null && evidence.DescriptorJson == descriptorJson && await IsStagedAsync(fileSystem, workspaceIdentity, artifactDigest.Value, descriptor.Id.Value, descriptor.Version.Value, cancellationToken);
            return proved ? new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.Proved, "The exact immutable lifecycle target is staged and proved.") : new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.NotFound, "No matching proved immutable lifecycle target is staged.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            return new CapabilityLifecycleArtifactEvidence(CapabilityLifecycleArtifactEvidenceStatus.Unavailable, "Immutable lifecycle target evidence is unavailable.");
        }
    }

    /// <inheritdoc />
    public Task<CapabilityLifecycleTargetResolution> ResolveAsync(CapabilityLifecycleTargetResolutionRequest request, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => ResolveTargetCoreAsync(request, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecycleTargetResolution> ResolveTargetCoreAsync(CapabilityLifecycleTargetResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CapabilityId is null || request.Kind is not CapabilityLifecycleOperationKind.Enable and not CapabilityLifecycleOperationKind.Upgrade)
        {
            return TargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "Only enable and upgrade may resolve staged lifecycle targets.");
        }

        try
        {
            await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var stagedRoot = Path.Combine(_paths.CapabilityArtifactsPath, "staged");
            if (!fileSystem.TryEnumerateStrictDirectories(stagedRoot, MaximumStagedArtifacts, out var digestNames))
            {
                return TargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "The bounded staged artifact directory quota was exceeded.");
            }

            var matches = new List<(CapabilityDescriptor Descriptor, CapabilityIntegrityDigest Digest)>();
            var aggregateEvidenceBytes = 0;
            long aggregateContentBytes = 0;
            foreach (var digestName in digestNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (digestName.Length != 64 || digestName.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || !CapabilityIntegrityDigest.TryParse("sha256:" + digestName, out var digest, out _))
                {
                    throw new FormatException("A staged artifact directory does not use one canonical digest identity.");
                }
                var candidate = await ReadStagedCandidateAsync(fileSystem, workspaceIdentity, digest!, aggregateEvidenceBytes, aggregateContentBytes, cancellationToken);
                aggregateEvidenceBytes = checked(aggregateEvidenceBytes + candidate.EvidenceBytes);
                aggregateContentBytes = checked(aggregateContentBytes + candidate.ContentBytes);
                if (aggregateEvidenceBytes > MaximumAggregateEvidenceBytes)
                {
                    return TargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "The bounded aggregate staged evidence quota was exceeded.");
                }
                if (aggregateContentBytes > MaximumAggregateContentBytes)
                {
                    return TargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "The bounded aggregate staged content quota was exceeded.");
                }
                if (candidate.Descriptor.Id.Equals(request.CapabilityId) && (request.TargetVersion is null || candidate.Descriptor.Version.Equals(request.TargetVersion)))
                {
                    matches.Add((candidate.Descriptor, digest!));
                }
            }

            return matches.Count switch
            {
                0 => TargetResolution(CapabilityLifecycleTargetResolutionStatus.NotFound, null, null, "A complete proved staged-artifact scan found no matching lifecycle target."),
                1 => TargetResolution(CapabilityLifecycleTargetResolutionStatus.Available, matches[0].Descriptor, matches[0].Digest, "Exactly one server-owned staged lifecycle target is available."),
                _ => TargetResolution(CapabilityLifecycleTargetResolutionStatus.Ambiguous, null, null, "Multiple distinct proved staged lifecycle targets matched; no lexical target was selected.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return TargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "The complete staged lifecycle target set could not be proved safely.");
        }
    }

    private async Task<(CapabilityDescriptor Descriptor, int EvidenceBytes, int ContentBytes)> ReadStagedCandidateAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, CapabilityIntegrityDigest digest, int aggregateEvidenceBytes, long aggregateContentBytes, CancellationToken cancellationToken)
    {
        var root = Path.Combine(_paths.CapabilityArtifactsPath, "staged", digest.Value["sha256:".Length..]);
        var evidencePath = Path.Combine(root, "artifact.evidence.json");
        var remainingEvidenceBytes = MaximumAggregateEvidenceBytes - aggregateEvidenceBytes;
        if (remainingEvidenceBytes <= 0)
        {
            throw new IOException("The bounded aggregate staged evidence quota is exhausted.");
        }
        var remainingContentBytes = MaximumAggregateContentBytes - aggregateContentBytes;
        if (remainingContentBytes <= 0)
        {
            throw new IOException("The bounded aggregate staged content quota is exhausted.");
        }
        var evidenceBytes = await fileSystem.TryReadAllBytesBoundAsync(evidencePath, Math.Min(MaximumActivationBytes, remainingEvidenceBytes), cancellationToken) ?? throw new FormatException("A staged artifact is missing its canonical evidence document.");
        var evidence = DeserializeStrict<CapabilityArtifactEvidenceDocument>(evidenceBytes) ?? throw new FormatException("A staged artifact evidence document is malformed.");
        if (!CapabilityDescriptorJson.TryDeserialize(evidence.DescriptorJson, out var descriptor, out _) || descriptor is null || !CapabilityDescriptorJson.TrySerialize(descriptor, out var canonicalDescriptor, out _) || canonicalDescriptor != evidence.DescriptorJson)
        {
            throw new FormatException("A staged artifact descriptor is malformed or noncanonical.");
        }
        if (!Enum.TryParse<CapabilityArtifactSourceKind>(evidence.SourceKind, ignoreCase: false, out var sourceKind) || !Enum.IsDefined(sourceKind) || !Enum.TryParse<CapabilityArtifactUpdatePolicy>(evidence.UpdatePolicy, ignoreCase: false, out var updatePolicy) || !Enum.IsDefined(updatePolicy) || !CapabilityPlatform.TryParse(evidence.Platform, out var platform, out _) || !CapabilityIntegrityDigest.TryParse(evidence.Checksum, out var checksum, out _))
        {
            throw new FormatException("A staged artifact manifest contains malformed typed evidence.");
        }
        var source = new CapabilityArtifactSourceReference(sourceKind, evidence.SourceUri, evidence.SourceRevision, updatePolicy);
        var manifest = new CapabilityArtifactManifest(evidence.SchemaVersion, descriptor, source, checksum!, evidence.Signature, platform!, evidence.EntryPoint, evidence.Arguments, evidence.Dependencies);
        if (evidence.SchemaVersion != 1 || !CapabilityArtifactManifestValidator.Validate(manifest).IsValid || evidence.Checksum != digest.Value || evidence.CapabilityId != descriptor.Id.Value || evidence.CapabilityVersion != descriptor.Version.Value || evidence.ProviderId != descriptor.Implementation.ProviderId.Value || evidence.ImplementationId != descriptor.Implementation.ImplementationId || descriptor.Provenance.Integrity is { } integrity && !integrity.FixedTimeEquals(digest) || evidence.TrustStatus != CapabilityArtifactTrustStatus.Verified.ToString() || string.IsNullOrWhiteSpace(evidence.Verifier) || evidence.ManifestPolicyPin != CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(manifest).Value || evidence.ContentDigest != ComputeEvidenceDigest(evidence) || string.IsNullOrWhiteSpace(evidence.AuthenticationTag) || !await _trustProvider.VerifyStagedEvidenceAsync(workspaceIdentity, digest.Value, evidence.ContentDigest, evidence.AuthenticationTag, cancellationToken))
        {
            throw new FormatException("A staged artifact failed canonical identity, content, or server trust proof.");
        }

        ValidateStagedArtifactShape(fileSystem, root, evidence.EntryPoint);
        var contentPath = Path.Combine(root, evidence.EntryPoint.Replace('/', Path.DirectorySeparatorChar));
        var content = await fileSystem.TryReadAllBytesBoundAsync(contentPath, (int)Math.Min(CapabilityArtifactManifestValidator.MaximumArtifactBytes, remainingContentBytes), cancellationToken) ?? throw new FormatException("A staged artifact is missing its immutable content.");
        if (!digest.FixedTimeEquals(CapabilityIntegrityDigest.Compute(content)))
        {
            throw new FormatException("Staged artifact content does not match its canonical digest identity.");
        }
        return (descriptor, evidenceBytes.Length, content.Length);
    }

    private static void ValidateStagedArtifactShape(CapabilityCatalogPathSession fileSystem, string root, string entryPoint)
    {
        var segments = entryPoint.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Length + 1 > MaximumStagedArtifactEntries)
        {
            throw new FormatException("The staged artifact entry-point shape exceeds its bound.");
        }

        var current = root;
        for (var index = 0; index < segments.Length; index++)
        {
            var entries = fileSystem.EnumerateBoundEntries(current, MaximumStagedArtifactEntries);
            var expectedNames = index == 0 ? new[] { "artifact.evidence.json", segments[index] } : new[] { segments[index] };
            if (!entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(expectedNames.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal))
            {
                throw new FormatException("A staged artifact contains unexpected or missing filesystem evidence.");
            }
            var target = entries.Single(entry => entry.Name == segments[index]);
            var expectedKind = index == segments.Length - 1 ? CapabilityCatalogDirectoryEntryKind.RegularFile : CapabilityCatalogDirectoryEntryKind.Directory;
            if (target.Kind != expectedKind)
            {
                throw new FormatException("A staged artifact entry point has an unexpected filesystem shape.");
            }
            current = Path.Combine(current, segments[index]);
        }
    }

    /// <inheritdoc />
    public Task<CapabilityExecutableArtifactResolution> ResolveAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => ResolveCoreAsync(invocation, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityExecutableArtifactResolution> ResolveCoreAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!CapabilityArtifactManifestValidator.Validate(invocation.Manifest).IsValid || invocation.ExpectedActivationRevision < 1)
        {
            return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Incompatible, null, "The executable activation request is invalid.");
        }
        CapabilityCatalogPathSession? fileSystem = null;
        try
        {
            CapabilityLifecycleState? lifecycleState = null;
            if (_lifecycleStore is not null)
            {
                var lifecycle = await _lifecycleStore.ReadAsync(invocation.Manifest.Descriptor.Id, cancellationToken);
                if (lifecycle.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable)
                {
                    return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The requested lifecycle state is unavailable.");
                }
                if (lifecycle.Status == CapabilityLifecycleReadStatus.Available && lifecycle.State is null)
                {
                    return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The requested lifecycle state is incomplete.");
                }
                lifecycleState = lifecycle.State;
                if (lifecycleState is not null && (!lifecycleState.IsEnabled || lifecycleState.IsRemoved || lifecycleState.Revision != invocation.ExpectedActivationRevision || !lifecycleState.ArtifactDigest.FixedTimeEquals(invocation.Manifest.Checksum)))
                {
                    return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The requested artifact is not the enabled current lifecycle target.");
                }
            }
            fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
            var activationState = lifecycleState is null ? await LoadProvedForMutationAsync(fileSystem, cancellationToken) : null;
            var activation = activationState?.Entries.SingleOrDefault(entry => entry.CapabilityId == invocation.Manifest.Descriptor.Id.Value);
            if (lifecycleState is null && (activationState is null || activation is null || activationState.Revision != invocation.ExpectedActivationRevision || activation.ArtifactDigest != invocation.Manifest.Checksum.Value))
            {
                return new CapabilityExecutableArtifactResolution(CapabilityExecutableAvailabilityStatus.Unavailable, null, "The requested artifact is not the current proved activation.");
            }
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var expectedPin = CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(invocation.Manifest).Value;
            var artifactDigest = lifecycleState?.ArtifactDigest.Value ?? activation!.ArtifactDigest;
            var root = Path.Combine(_paths.CapabilityArtifactsPath, "staged", artifactDigest["sha256:".Length..]);
            var evidencePath = Path.Combine(root, "artifact.evidence.json");
            var evidenceBytes = await fileSystem.ReadAllBytesAsync(evidencePath, MaximumActivationBytes, cancellationToken);
            var evidence = DeserializeStrict<CapabilityArtifactEvidenceDocument>(evidenceBytes);
            if (evidence is null || evidence.ManifestPolicyPin != expectedPin || !await IsStagedAsync(fileSystem, workspaceIdentity, artifactDigest, invocation.Manifest.Descriptor.Id.Value, invocation.Manifest.Descriptor.Version.Value, cancellationToken))
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
                fileSystem.ReleaseLock();
                var lease = new CapabilityExecutableArtifactLease(fileSystem, executable, root, executablePath, actualDigest, lifecycleState?.Revision ?? activationState!.Revision, _authorityTransaction, transactionCancellationToken => ValidateLaunchAsync(invocation, transactionCancellationToken));
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

    private async Task<bool> ValidateLaunchAsync(CapabilityExecutableInvocation invocation, CancellationToken cancellationToken)
    {
        if (_lifecycleStore is not null)
        {
            var lifecycle = await _lifecycleStore.ReadAsync(invocation.Manifest.Descriptor.Id, cancellationToken);
            if (lifecycle.Status is CapabilityLifecycleReadStatus.RecoveredLastProved or CapabilityLifecycleReadStatus.Unavailable || lifecycle.Status == CapabilityLifecycleReadStatus.Available && lifecycle.State is null)
            {
                return false;
            }
            if (lifecycle.State is { } state)
            {
                return state.IsEnabled && !state.IsRemoved && state.Revision == invocation.ExpectedActivationRevision && state.ArtifactDigest.FixedTimeEquals(invocation.Manifest.Checksum);
            }
        }

        await using var fileSystem = await AcquireAsync(createRoot: false, cancellationToken);
        var activationState = await LoadProvedForMutationAsync(fileSystem, cancellationToken);
        var activation = activationState?.Entries.SingleOrDefault(entry => entry.CapabilityId == invocation.Manifest.Descriptor.Id.Value);
        return activationState is not null && activation is not null && activationState.Revision == invocation.ExpectedActivationRevision && activation.ArtifactDigest == invocation.Manifest.Checksum.Value;
    }

    private async Task<CapabilityLifecycleReadResult?> ReadLifecycleBoundaryAsync(CapabilityId capabilityId, CancellationToken cancellationToken) => _lifecycleStore is null ? null : await _lifecycleStore.ReadAsync(capabilityId, cancellationToken);

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
            if (!CapabilityDescriptorJson.TryDeserialize(evidence.DescriptorJson, out var descriptor, out _) || descriptor is null || descriptor.Id.Value != evidence.CapabilityId || descriptor.Version.Value != evidence.CapabilityVersion || !CapabilityDescriptorJson.TrySerialize(descriptor, out var canonicalDescriptor, out _) || canonicalDescriptor != evidence.DescriptorJson)
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
        _ = CapabilityDescriptorJson.TrySerialize(request.Manifest.Descriptor, out var descriptorJson, out _);
        var evidence = new CapabilityArtifactEvidenceDocument(1, request.Manifest.Descriptor.Id.Value, request.Manifest.Descriptor.Version.Value, descriptorJson!, request.Manifest.Descriptor.Implementation.ProviderId.Value, request.Manifest.Descriptor.Implementation.ImplementationId, request.Manifest.Source.Kind.ToString(), request.Manifest.Source.Uri, request.Manifest.Source.Revision, request.Manifest.Source.UpdatePolicy.ToString(), request.Manifest.Checksum.Value, request.Manifest.Signature, request.Manifest.Platform.ToString(), request.Manifest.EntryPoint, request.Manifest.Arguments, request.Manifest.Dependencies, request.Trust.Status.ToString(), request.Trust.Verifier, CapabilityArtifactManifestCanonicalizer.ComputePolicyPin(request.Manifest).Value, string.Empty, string.Empty);
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

    private static CapabilityLifecycleTargetResolution TargetResolution(CapabilityLifecycleTargetResolutionStatus status, CapabilityDescriptor? descriptor, CapabilityIntegrityDigest? digest, string detail) => new(status, descriptor, digest, detail);

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
