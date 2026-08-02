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

/// <summary>Persists one authenticated schema-1 capability lifecycle aggregate under one cross-process mutation lock.</summary>
/// <remarks>The aggregate owns lifecycle previews, dependent evidence, current state, tombstones, degradation evidence, history, and never-evicted operation receipts. It never rewrites immutable artifacts or domain-owned dependents.</remarks>
public sealed class CapabilityLifecycleMutationStore : ICapabilityLifecycleMutationStore
{
    private const int MaximumArtifactBytes = 4 * 1024 * 1024;
    private const int MaximumEntries = 256;
    private const int MaximumDependents = 2_048;
    private const int MaximumOperations = 1_024;
    private const int MaximumHistoryPerEntry = 1_024;
    private static readonly JsonSerializerOptions _jsonOptions = CreateJsonOptions(writeIndented: true);
    private static readonly JsonSerializerOptions _hashOptions = CreateJsonOptions(writeIndented: false);
    private static readonly JsonDocumentOptions _documentOptions = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 64 };
    private readonly WorkspacePaths _paths;
    private readonly ICapabilityCatalogTrustProvider _trustProvider;
    private readonly ICapabilityLifecycleBaselineSource? _baselineSource;
    private readonly ICapabilityLifecycleArtifactEvidenceSource? _artifactEvidenceSource;
    private readonly CapabilityCatalogPathGuard _guard;
    private readonly TimeProvider _timeProvider;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates a read-only lifecycle store bound to one workspace and server-owned trust provider.</summary>
    public CapabilityLifecycleMutationStore(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        _paths = paths;
        _trustProvider = trustProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _guard = new CapabilityCatalogPathGuard(paths.CapabilityCatalogPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <summary>Creates a mutable lifecycle store bound to current baseline and immutable artifact proof sources.</summary>
    public CapabilityLifecycleMutationStore(WorkspacePaths paths, ICapabilityCatalogTrustProvider trustProvider, ICapabilityLifecycleBaselineSource baselineSource, ICapabilityLifecycleArtifactEvidenceSource artifactEvidenceSource, TimeProvider? timeProvider = null, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trustProvider);
        ArgumentNullException.ThrowIfNull(baselineSource);
        ArgumentNullException.ThrowIfNull(artifactEvidenceSource);
        _paths = paths;
        _trustProvider = trustProvider;
        _baselineSource = baselineSource;
        _artifactEvidenceSource = artifactEvidenceSource;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _guard = new CapabilityCatalogPathGuard(paths.CapabilityCatalogPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance);
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <inheritdoc />
    public Task<CapabilityLifecycleReadResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => ReadCoreAsync(capabilityId, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecycleReadResult> ReadCoreAsync(CapabilityId capabilityId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capabilityId);
        try
        {
            await using var fileSystem = await AcquireAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var loaded = await LoadForReadAsync(fileSystem, workspaceIdentity, cancellationToken);
            if (loaded.Document is null)
            {
                return ReadResult(CapabilityLifecycleReadStatus.Unavailable, null, null, "No authenticated lifecycle aggregate is available.");
            }
            var entry = loaded.Document.Entries.SingleOrDefault(candidate => candidate.CapabilityId == capabilityId.Value);
            if (entry is null)
            {
                return loaded.Recovered ? ReadResult(CapabilityLifecycleReadStatus.RecoveredLastProved, loaded.Document, null, "The recovered lifecycle proof cannot prove that this capability remains unregistered.") : ReadResult(CapabilityLifecycleReadStatus.NotFound, loaded.Document, null, "The capability is not registered in the current lifecycle aggregate.");
            }
            return ReadResult(loaded.Recovered ? CapabilityLifecycleReadStatus.RecoveredLastProved : CapabilityLifecycleReadStatus.Available, loaded.Document, entry, loaded.Recovered ? "The last proved lifecycle aggregate was recovered read-only." : "The current authenticated lifecycle state is available.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return ReadResult(CapabilityLifecycleReadStatus.Unavailable, null, null, "Lifecycle state is unavailable.");
        }
    }

    /// <inheritdoc />
    public Task<CapabilityLifecyclePreview> PreviewAsync(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => PreviewCoreAsync(request, baseline, dependents, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecyclePreview> PreviewCoreAsync(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dependents);
        var invalid = ValidateRequest(request);
        if (invalid is not null)
        {
            return PreviewResult(CapabilityLifecyclePreviewStatus.Invalid, request, invalid);
        }
        if (_baselineSource is null || _artifactEvidenceSource is null)
        {
            return PreviewResult(CapabilityLifecyclePreviewStatus.Unavailable, request, "Lifecycle mutation authority is unavailable in this read-only composition.");
        }

        try
        {
            await using var fileSystem = await AcquireAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var current = await LoadForMutationAsync(fileSystem, workspaceIdentity, cancellationToken);
            if (current is null)
            {
                return PreviewResult(CapabilityLifecyclePreviewStatus.Unavailable, request, "The current lifecycle aggregate cannot be proved.");
            }

            var requestHash = ComputeRequestHash(request);
            var existingOperation = current.Operations.SingleOrDefault(operation => operation.OperationId == request.OperationId);
            if (existingOperation is not null)
            {
                return existingOperation.RequestHash == requestHash ? MapPreview(current.WorkspaceIdentity, existingOperation, CapabilityLifecyclePreviewStatus.Replayed, "The exact lifecycle preview operation was replayed.") : PreviewResult(CapabilityLifecyclePreviewStatus.Conflict, request, "The operation identity is already bound to different lifecycle intent.", workspaceIdentity);
            }
            if (!IsValidSnapshot(dependents))
            {
                return PreviewResult(CapabilityLifecyclePreviewStatus.Unavailable, request, "The complete dependent set is unavailable; lifecycle preview fails closed.");
            }

            var entries = current.Entries.ToList();
            var entry = entries.SingleOrDefault(candidate => candidate.CapabilityId == request.CapabilityId.Value);
            var observedBaseline = await _baselineSource.ReadAsync(request.CapabilityId, cancellationToken);
            if (entry is null)
            {
                if (!SameBaseline(baseline, observedBaseline) || !TryMapBaseline(request.CapabilityId, observedBaseline, current.Generation, out entry))
                {
                    return PreviewResult(CapabilityLifecyclePreviewStatus.NotFound, request, "The capability has no matching current proved baseline for first lifecycle registration.", workspaceIdentity);
                }
                if (entries.Count >= MaximumEntries)
                {
                    return PreviewResult(CapabilityLifecyclePreviewStatus.Unavailable, request, "The bounded lifecycle capability index is full.", workspaceIdentity);
                }
                entries.Add(entry!);
            }
            else if (!SameBaseline(baseline, observedBaseline) || !MatchesBaseline(entry, observedBaseline))
            {
                return PreviewResult(CapabilityLifecyclePreviewStatus.Conflict, request, "The current catalog or activation baseline drifted from lifecycle registration evidence.", workspaceIdentity);
            }

            if (current.Operations.Count >= MaximumOperations)
            {
                return PreviewResult(CapabilityLifecyclePreviewStatus.Unavailable, request, "The never-evicted lifecycle operation ledger reached its configured bound.", workspaceIdentity);
            }

            var target = ResolveTarget(request, entry!);
            if (target.Status is not null)
            {
                return PreviewResult(target.Status.Value, request, target.Detail, workspaceIdentity);
            }
            if (request.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback)
            {
                var artifact = await _artifactEvidenceSource.VerifyAsync(target.Descriptor!, target.ArtifactDigest!, cancellationToken);
                if (artifact.Status != CapabilityLifecycleArtifactEvidenceStatus.Proved)
                {
                    return PreviewResult(artifact.Status == CapabilityLifecycleArtifactEvidenceStatus.NotFound ? CapabilityLifecyclePreviewStatus.NotFound : CapabilityLifecyclePreviewStatus.Unavailable, request, artifact.Detail, workspaceIdentity);
                }
            }

            var dependentDocuments = dependents.Dependents.Select(Map).ToArray();
            var dependentRevision = current.DependentSetHash == dependents.Hash ? current.DependentSetRevision : checked(current.DependentSetRevision + 1);
            var impacts = ComputeImpacts(request, target.Descriptor, dependents.Dependents);
            var previewRevision = checked(current.Generation + 1);
            var targetDescriptorJson = request.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback ? SerializeDescriptor(target.Descriptor) : null;
            var targetArtifactDigest = request.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback ? target.ArtifactDigest?.Value : null;
            var previewHash = ComputePreviewHash(workspaceIdentity, request, requestHash, observedBaseline!.CatalogRevision, observedBaseline.ActivationRevision, targetDescriptorJson, targetArtifactDigest, previewRevision, dependentRevision, dependents.Hash, impacts);
            var operation = new CapabilityLifecycleOperationDocument(request.OperationId, requestHash, request.Kind, request.CapabilityId.Value, targetDescriptorJson, targetArtifactDigest, observedBaseline.CatalogRevision, observedBaseline.ActivationRevision, previewRevision, dependentRevision, dependents.Hash, previewHash, impacts, null, null, false);
            var candidate = Seal(current with { Generation = previewRevision, DependentSetRevision = dependentRevision, DependentSetHash = dependents.Hash, Dependents = dependentDocuments, Entries = entries.OrderBy(item => item.CapabilityId, StringComparer.Ordinal).ToArray(), Operations = current.Operations.Append(operation).ToArray() });
            await CommitAsync(fileSystem, current, candidate, cancellationToken);
            return MapPreview(workspaceIdentity, operation, CapabilityLifecyclePreviewStatus.Ready, impacts.Any(impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Blocked) ? "The preview is deterministic, but required dependents block the proposed transition." : "The deterministic lifecycle impact preview is ready.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return PreviewResult(CapabilityLifecyclePreviewStatus.Unavailable, request, "Lifecycle preview is unavailable; the last proved aggregate was preserved.");
        }
    }

    /// <inheritdoc />
    public Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => MutateCoreAsync(preview, baseline, dependents, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecycleMutationResult> MutateCoreAsync(CapabilityLifecyclePreview preview, CapabilityLifecycleBaseline? baseline, CapabilityDependentIndexSnapshot dependents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(dependents);
        if (preview.Status is not CapabilityLifecyclePreviewStatus.Ready and not CapabilityLifecyclePreviewStatus.Replayed || preview.CapabilityId is null || !CapabilityArtifactManifestValidator.IsOperationId(preview.OperationId) || !Enum.IsDefined(preview.Kind) || preview.LifecycleRevision < 1 || preview.DependentSetRevision < 0 || !IsDigest(preview.PreviewHash) || !IsDigest(preview.DependentSetHash))
        {
            return Result(CapabilityLifecycleMutationStatus.Invalid, null, null, false, "Only an exact ready lifecycle preview may be mutated.");
        }
        if (_baselineSource is null || _artifactEvidenceSource is null)
        {
            return Result(CapabilityLifecycleMutationStatus.Unavailable, null, null, false, "Lifecycle mutation authority is unavailable in this read-only composition.");
        }

        try
        {
            await using var fileSystem = await AcquireAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var current = await LoadForMutationAsync(fileSystem, workspaceIdentity, cancellationToken);
            if (current is null)
            {
                return Result(CapabilityLifecycleMutationStatus.Unavailable, null, null, false, "The current lifecycle aggregate cannot be proved.");
            }
            var operation = current.Operations.SingleOrDefault(candidate => candidate.OperationId == preview.OperationId);
            if (operation is null || operation.PreviewHash != preview.PreviewHash || operation.CapabilityId != preview.CapabilityId.Value || operation.Kind != preview.Kind || operation.BaselineCatalogRevision != preview.BaselineCatalogRevision || operation.BaselineActivationRevision != preview.BaselineActivationRevision || operation.PreviewRevision != preview.LifecycleRevision || operation.DependentSetRevision != preview.DependentSetRevision || operation.DependentSetHash != preview.DependentSetHash || operation.TargetDescriptorJson != SerializeDescriptor(preview.TargetDescriptor) || operation.TargetArtifactDigest != preview.TargetArtifactDigest?.Value || !operation.Impacts.SequenceEqual(preview.Impacts) || preview.WorkspaceIdentity != workspaceIdentity)
            {
                return Result(CapabilityLifecycleMutationStatus.Conflict, Current(current, preview.CapabilityId.Value), current.Generation, false, "The supplied preview is forged, unknown, or bound to different lifecycle intent.");
            }
            if (operation.Outcome is { } terminal)
            {
                return Result(CapabilityLifecycleMutationStatus.Replayed, Current(current, operation.CapabilityId), operation.ResultRevision, operation.OutcomeAuditPending, $"The exact terminal {terminal.ToString().ToLowerInvariant()} lifecycle operation was replayed.", terminal);
            }
            if (!IsValidSnapshot(dependents))
            {
                return Result(CapabilityLifecycleMutationStatus.Unavailable, null, null, false, "The complete dependent set is unavailable; mutation fails closed.");
            }

            if (current.Generation != operation.PreviewRevision || current.DependentSetRevision != operation.DependentSetRevision || current.DependentSetHash != operation.DependentSetHash || dependents.Hash != operation.DependentSetHash)
            {
                return await CompleteWithoutMutationAsync(fileSystem, current, operation, CapabilityLifecycleMutationStatus.Conflict, dependents, "Lifecycle or dependent state changed after preview; no capability state was changed.", cancellationToken);
            }

            var entry = current.Entries.SingleOrDefault(candidate => candidate.CapabilityId == operation.CapabilityId);
            if (entry is null)
            {
                return await CompleteWithoutMutationAsync(fileSystem, current, operation, CapabilityLifecycleMutationStatus.NotFound, dependents, "The capability disappeared before mutation.", cancellationToken);
            }
            var observedBaseline = await _baselineSource.ReadAsync(preview.CapabilityId, cancellationToken);
            if (!SameBaseline(baseline, observedBaseline) || !MatchesBaseline(entry, observedBaseline) || operation.BaselineCatalogRevision != observedBaseline?.CatalogRevision || operation.BaselineActivationRevision != observedBaseline?.ActivationRevision)
            {
                return await CompleteWithoutMutationAsync(fileSystem, current, operation, CapabilityLifecycleMutationStatus.Conflict, dependents, "The current catalog or activation baseline changed after preview; no capability state was changed.", cancellationToken);
            }
            if (operation.Impacts.Any(impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Blocked))
            {
                return await CompleteWithoutMutationAsync(fileSystem, current, operation, CapabilityLifecycleMutationStatus.Blocked, dependents, "At least one required dependent blocks the lifecycle transition.", cancellationToken);
            }
            var target = ResolveTarget(operation, entry);
            if (target.Status is not null)
            {
                return await CompleteWithoutMutationAsync(fileSystem, current, operation, CapabilityLifecycleMutationStatus.NotFound, dependents, target.Detail, cancellationToken);
            }
            if (operation.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback)
            {
                var artifact = await _artifactEvidenceSource.VerifyAsync(target.Descriptor!, target.ArtifactDigest!, cancellationToken);
                if (artifact.Status != CapabilityLifecycleArtifactEvidenceStatus.Proved)
                {
                    return Result(artifact.Status == CapabilityLifecycleArtifactEvidenceStatus.NotFound ? CapabilityLifecycleMutationStatus.NotFound : CapabilityLifecycleMutationStatus.Unavailable, Map(entry), current.Generation, false, artifact.Detail);
                }
            }
            if (entry.History.Count >= MaximumHistoryPerEntry)
            {
                return Result(CapabilityLifecycleMutationStatus.Unavailable, Map(entry), current.Generation, false, "The immutable lifecycle history reached its configured bound.");
            }

            var revision = checked(current.Generation + 1);
            var now = _timeProvider.GetUtcNow().ToUniversalTime();
            var history = entry.History.Append(new CapabilityLifecycleHistoryDocument(entry.DescriptorJson, entry.ArtifactDigest, entry.IsEnabled, entry.IsRemoved, entry.Revision, entry.LastOperationId, entry.UpdatedAtUtc)).ToArray();
            var degradations = operation.Impacts.Where(impact => impact.Outcome == CapabilityLifecycleImpactOutcome.Degraded).Select(impact => new CapabilityLifecycleDegradationDocument(entry.CapabilityId, operation.OperationId, impact.DependentKind.ToString(), impact.DependentIdentity, impact.DependentRevision, impact.CompatibleVersionRange, now)).ToArray();
            var rollbackState = operation.Kind == CapabilityLifecycleOperationKind.Rollback ? entry.History.Last() : null;
            var updated = entry with { DescriptorJson = SerializeDescriptor(target.Descriptor)!, ArtifactDigest = target.ArtifactDigest!.Value, IsEnabled = rollbackState?.IsEnabled ?? operation.Kind is not CapabilityLifecycleOperationKind.Disable and not CapabilityLifecycleOperationKind.Remove, IsRemoved = rollbackState?.IsRemoved ?? operation.Kind == CapabilityLifecycleOperationKind.Remove, Revision = revision, LastOperationId = operation.OperationId, UpdatedAtUtc = now, History = history, Degradations = degradations };
            var completed = operation with { Outcome = CapabilityLifecycleMutationStatus.Applied, ResultRevision = revision, OutcomeAuditPending = true };
            var candidate = Seal(current with { Generation = revision, Entries = current.Entries.Where(candidate => candidate.CapabilityId != entry.CapabilityId).Append(updated).OrderBy(candidate => candidate.CapabilityId, StringComparer.Ordinal).ToArray(), Operations = Replace(current.Operations, completed) });
            await CommitAsync(fileSystem, current, candidate, cancellationToken);
            return Result(CapabilityLifecycleMutationStatus.Applied, Map(updated), revision, true, "The lifecycle transition was applied atomically with retained history and explicit optional degradation evidence.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return Result(CapabilityLifecycleMutationStatus.Unavailable, null, null, false, "Atomic lifecycle mutation is unavailable; the last proved aggregate was preserved.");
        }
    }

    /// <inheritdoc />
    public Task<CapabilityLifecycleAuditMarkStatus> MarkOutcomeAuditedAsync(string operationId, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => MarkOutcomeAuditedCoreAsync(operationId, transactionCancellationToken), cancellationToken);

    private async Task<CapabilityLifecycleAuditMarkStatus> MarkOutcomeAuditedCoreAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!CapabilityArtifactManifestValidator.IsOperationId(operationId))
        {
            return CapabilityLifecycleAuditMarkStatus.NotFound;
        }
        try
        {
            await using var fileSystem = await AcquireAsync(cancellationToken);
            var workspaceIdentity = CapabilityCatalogWorkspaceIdentity.CreateFromPhysicalIdentity(fileSystem.PhysicalIdentityMaterial);
            var current = await LoadForMutationAsync(fileSystem, workspaceIdentity, cancellationToken);
            var operation = current?.Operations.SingleOrDefault(candidate => candidate.OperationId == operationId && candidate.Outcome is not null);
            if (current is null || operation is null)
            {
                return CapabilityLifecycleAuditMarkStatus.NotFound;
            }
            if (!operation.OutcomeAuditPending)
            {
                return CapabilityLifecycleAuditMarkStatus.NoChange;
            }
            var candidate = Seal(current with { Generation = checked(current.Generation + 1), Operations = Replace(current.Operations, operation with { OutcomeAuditPending = false }) });
            await CommitAsync(fileSystem, current, candidate, cancellationToken);
            return CapabilityLifecycleAuditMarkStatus.Applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsAvailabilityFailure(exception))
        {
            return CapabilityLifecycleAuditMarkStatus.Unavailable;
        }
    }

    private async Task<CapabilityLifecycleMutationResult> CompleteWithoutMutationAsync(CapabilityCatalogPathSession fileSystem, CapabilityLifecycleDocument current, CapabilityLifecycleOperationDocument operation, CapabilityLifecycleMutationStatus outcome, CapabilityDependentIndexSnapshot dependents, string detail, CancellationToken cancellationToken)
    {
        var revision = checked(current.Generation + 1);
        var completed = operation with { Outcome = outcome, ResultRevision = revision, OutcomeAuditPending = true };
        var dependentRevision = current.DependentSetHash == dependents.Hash ? current.DependentSetRevision : checked(current.DependentSetRevision + 1);
        var candidate = Seal(current with { Generation = revision, DependentSetRevision = dependentRevision, DependentSetHash = dependents.Hash, Dependents = dependents.Dependents.Select(Map).ToArray(), Operations = Replace(current.Operations, completed) });
        await CommitAsync(fileSystem, current, candidate, cancellationToken);
        return Result(outcome, Current(candidate, operation.CapabilityId), revision, true, detail);
    }

    private async Task<CapabilityCatalogPathSession> AcquireAsync(CancellationToken cancellationToken)
    {
        var session = await _guard.TryAcquireExclusiveSessionAsync(_paths.CapabilityLifecycleLockPath, createRoot: false, cancellationToken) ?? throw new IOException("The capability lifecycle root is unavailable.");
        session.PrepareDirectory(_paths.CapabilityCatalogPath);
        return session;
    }

    private async Task<CapabilityLifecycleDocument?> LoadForMutationAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, CancellationToken cancellationToken)
    {
        var trust = await _trustProvider.ReadAsync(TrustIdentity(workspaceIdentity), cancellationToken);
        var primary = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityLifecycleDocumentPath, cancellationToken);
        var proof = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityLifecycleProofPath, cancellationToken);
        if (primary is null && proof is null)
        {
            var empty = Empty(workspaceIdentity);
            return trust is null || MatchesCurrent(empty, trust) ? empty : null;
        }
        if (trust is null)
        {
            return null;
        }
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return primary;
        }
        if (proof is null || !MatchesCurrent(proof, trust))
        {
            return null;
        }
        if (primary is not null && primary.Generation == checked(proof.Generation + 1))
        {
            var advanced = await _trustProvider.AdvanceAsync(TrustIdentity(workspaceIdentity), proof.Generation, proof.ContentDigest, primary.Generation, primary.ContentDigest, cancellationToken);
            if (!MatchesCurrent(primary, advanced))
            {
                return null;
            }
            await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityLifecycleProofPath, await SerializeAsync(TrustIdentity(workspaceIdentity), primary, cancellationToken), cancellationToken);
            return primary;
        }
        return proof;
    }

    private async Task<(CapabilityLifecycleDocument? Document, bool Recovered)> LoadForReadAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, CancellationToken cancellationToken)
    {
        var trust = await _trustProvider.ReadAsync(TrustIdentity(workspaceIdentity), cancellationToken);
        var primary = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityLifecycleDocumentPath, cancellationToken);
        var proof = await ReadDocumentAsync(fileSystem, workspaceIdentity, _paths.CapabilityLifecycleProofPath, cancellationToken);
        if (primary is null && proof is null && trust is null)
        {
            return (Empty(workspaceIdentity), false);
        }
        if (trust is null)
        {
            return (null, false);
        }
        if (primary is not null && MatchesCurrent(primary, trust))
        {
            return (primary, false);
        }
        if (proof is not null && (MatchesCurrent(proof, trust) || MatchesPrevious(proof, trust)))
        {
            return (proof, true);
        }
        return primary is not null && MatchesPrevious(primary, trust) ? (primary, true) : (null, false);
    }

    private async Task<CapabilityLifecycleDocument?> ReadDocumentAsync(CapabilityCatalogPathSession fileSystem, string workspaceIdentity, string path, CancellationToken cancellationToken)
    {
        if (!fileSystem.FileExists(path))
        {
            return null;
        }
        try
        {
            var bytes = await fileSystem.ReadAllBytesAsync(path, MaximumArtifactBytes, cancellationToken);
            using var json = JsonDocument.Parse(bytes, _documentOptions);
            RejectDuplicateMembers(json.RootElement);
            var document = JsonSerializer.Deserialize<CapabilityLifecycleDocument>(json.RootElement, _jsonOptions);
            if (document is null || !IsValid(document, workspaceIdentity) || !await _trustProvider.VerifyArtifactAsync(TrustIdentity(workspaceIdentity), document.Generation, document.ContentDigest, document.AuthenticationTag, cancellationToken))
            {
                return null;
            }
            return document;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task CommitAsync(CapabilityCatalogPathSession fileSystem, CapabilityLifecycleDocument current, CapabilityLifecycleDocument candidate, CancellationToken cancellationToken)
    {
        var workspaceIdentity = current.WorkspaceIdentity;
        var trustIdentity = TrustIdentity(workspaceIdentity);
        var trust = await _trustProvider.ReadAsync(trustIdentity, cancellationToken) ?? await _trustProvider.InitializeAsync(trustIdentity, 0, current.ContentDigest, cancellationToken);
        if (!MatchesCurrent(current, trust) || candidate.Generation != checked(current.Generation + 1))
        {
            throw new IOException("The server-owned lifecycle anchor changed before mutation.");
        }
        var currentJson = await SerializeAsync(trustIdentity, current, cancellationToken);
        var candidateJson = await SerializeAsync(trustIdentity, candidate, cancellationToken);
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityLifecycleProofPath, currentJson, cancellationToken);
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityLifecycleDocumentPath, candidateJson, cancellationToken);
        var advanced = await _trustProvider.AdvanceAsync(trustIdentity, current.Generation, current.ContentDigest, candidate.Generation, candidate.ContentDigest, cancellationToken);
        if (!MatchesCurrent(candidate, advanced))
        {
            throw new IOException("The server-owned lifecycle anchor did not accept the exact committed candidate.");
        }
        await fileSystem.WriteTextAtomicallyAsync(_paths.CapabilityLifecycleProofPath, candidateJson, cancellationToken);
    }

    private async Task<string> SerializeAsync(string trustIdentity, CapabilityLifecycleDocument document, CancellationToken cancellationToken)
    {
        var tag = await _trustProvider.AuthenticateArtifactAsync(trustIdentity, document.Generation, document.ContentDigest, cancellationToken);
        var json = JsonSerializer.Serialize(document with { AuthenticationTag = tag }, _jsonOptions) + Environment.NewLine;
        return Encoding.UTF8.GetByteCount(json) <= MaximumArtifactBytes ? json : throw new IOException("The bounded lifecycle aggregate size would be exceeded.");
    }

    private static string? ValidateRequest(CapabilityLifecyclePreviewRequest request)
    {
        if (!CapabilityArtifactManifestValidator.IsOperationId(request.OperationId) || request.CapabilityId is null || !Enum.IsDefined(request.Kind))
        {
            return "The lifecycle operation identity, kind, or capability target is invalid.";
        }
        if (request.Kind == CapabilityLifecycleOperationKind.Upgrade)
        {
            if (request.TargetDescriptor is null || request.TargetArtifactDigest is null || !CapabilityDescriptorValidator.Validate(request.TargetDescriptor).IsValid || !request.TargetDescriptor.Id.Equals(request.CapabilityId) || request.TargetDescriptor.Provenance.Integrity is { } integrity && !integrity.FixedTimeEquals(request.TargetArtifactDigest))
            {
                return "Upgrade requires one matching validated descriptor and exact immutable artifact digest.";
            }
        }
        else if (request.TargetDescriptor is not null || request.TargetArtifactDigest is not null)
        {
            return "Rollback, disable, and removal derive their target from proved lifecycle state and cannot accept replacement content.";
        }
        return null;
    }

    private static bool TryMapBaseline(CapabilityId capabilityId, CapabilityLifecycleBaseline? baseline, long registrationRevision, out CapabilityLifecycleEntryDocument? entry)
    {
        entry = null;
        if (baseline?.State is not { } state || baseline.CatalogRevision < 0 || baseline.ActivationRevision < 1 || registrationRevision < 0 || state.Descriptor is null || state.ArtifactDigest is null || !CapabilityDescriptorValidator.Validate(state.Descriptor).IsValid || !state.Descriptor.Id.Equals(capabilityId) || state.Revision < 0 || !CapabilityArtifactManifestValidator.IsOperationId(state.LastOperationId) || state.UpdatedAtUtc.Offset != TimeSpan.Zero || state.IsRemoved && state.IsEnabled)
        {
            return false;
        }
        entry = new CapabilityLifecycleEntryDocument(capabilityId.Value, SerializeDescriptor(state.Descriptor)!, state.ArtifactDigest.Value, state.IsEnabled, state.IsRemoved, registrationRevision, state.LastOperationId, state.UpdatedAtUtc, baseline.CatalogRevision, baseline.ActivationRevision, [], []);
        return true;
    }

    private static (CapabilityDescriptor? Descriptor, CapabilityIntegrityDigest? ArtifactDigest, CapabilityLifecyclePreviewStatus? Status, string Detail) ResolveTarget(CapabilityLifecyclePreviewRequest request, CapabilityLifecycleEntryDocument entry)
    {
        if (entry.IsRemoved && request.Kind != CapabilityLifecycleOperationKind.Rollback)
        {
            return (null, null, CapabilityLifecyclePreviewStatus.Invalid, "A tombstoned capability can only be restored through an exact proved rollback.");
        }
        if (request.Kind == CapabilityLifecycleOperationKind.Upgrade)
        {
            return (request.TargetDescriptor, request.TargetArtifactDigest, null, string.Empty);
        }
        if (request.Kind == CapabilityLifecycleOperationKind.Rollback)
        {
            var prior = entry.History.LastOrDefault();
            return prior is null || !TryParseDescriptor(prior.DescriptorJson, out var descriptor) || !CapabilityIntegrityDigest.TryParse(prior.ArtifactDigest, out var digest, out _) ? (null, null, CapabilityLifecyclePreviewStatus.NotFound, "Rollback requires an immediately preceding proved lifecycle state.") : (descriptor, digest, null, string.Empty);
        }
        return TryParseDescriptor(entry.DescriptorJson, out var current) && CapabilityIntegrityDigest.TryParse(entry.ArtifactDigest, out var currentDigest, out _) ? (current, currentDigest, null, string.Empty) : (null, null, CapabilityLifecyclePreviewStatus.Unavailable, "The current lifecycle state is malformed.");
    }

    private static (CapabilityDescriptor? Descriptor, CapabilityIntegrityDigest? ArtifactDigest, CapabilityLifecycleMutationStatus? Status, string Detail) ResolveTarget(CapabilityLifecycleOperationDocument operation, CapabilityLifecycleEntryDocument entry)
    {
        if (operation.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback)
        {
            return TryParseDescriptor(operation.TargetDescriptorJson, out var descriptor) && CapabilityIntegrityDigest.TryParse(operation.TargetArtifactDigest, out var digest, out _) ? (descriptor, digest, null, string.Empty) : (null, null, CapabilityLifecycleMutationStatus.NotFound, "The exact preview-bound upgrade or rollback artifact evidence is unavailable.");
        }
        return TryParseDescriptor(entry.DescriptorJson, out var current) && CapabilityIntegrityDigest.TryParse(entry.ArtifactDigest, out var currentDigest, out _) ? (current, currentDigest, null, string.Empty) : (null, null, CapabilityLifecycleMutationStatus.NotFound, "The current lifecycle target is unavailable.");
    }

    private static IReadOnlyList<CapabilityLifecycleImpact> ComputeImpacts(CapabilityLifecyclePreviewRequest request, CapabilityDescriptor? target, IEnumerable<CapabilityDependent> dependents)
    {
        var impacts = new List<CapabilityLifecycleImpact>();
        foreach (var dependent in dependents)
        {
            Add(dependent.Manifest.Required, CapabilityRequirementKind.Required);
            Add(dependent.Manifest.Optional, CapabilityRequirementKind.Optional);
            void Add(IEnumerable<CapabilityDependency> dependencies, CapabilityRequirementKind requirementKind)
            {
                foreach (var dependency in dependencies.Where(candidate => candidate.CapabilityId.Equals(request.CapabilityId)))
                {
                    var compatible = request.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback && target is not null && dependency.CompatibleVersionRange.Contains(target.Version);
                    var outcome = compatible ? CapabilityLifecycleImpactOutcome.Preserved : requirementKind == CapabilityRequirementKind.Required ? CapabilityLifecycleImpactOutcome.Blocked : CapabilityLifecycleImpactOutcome.Degraded;
                    impacts.Add(new CapabilityLifecycleImpact(dependent.Kind, dependent.Identity, dependent.Revision, requirementKind, dependency.CompatibleVersionRange.Value, compatible, dependent.AuthorityPosture, outcome));
                }
            }
        }
        return impacts.OrderBy(impact => impact.DependentKind).ThenBy(impact => impact.DependentIdentity, StringComparer.Ordinal).ThenBy(impact => impact.RequirementKind).ToArray();
    }

    private static string ComputeRequestHash(CapabilityLifecyclePreviewRequest request)
    {
        var descriptor = SerializeDescriptor(request.TargetDescriptor) ?? string.Empty;
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes($"capability-lifecycle-request-v1\n{request.OperationId}\n{(int)request.Kind}\n{request.CapabilityId.Value}\n{descriptor}\n{request.TargetArtifactDigest?.Value}")).Value;
    }

    private static string ComputePreviewHash(string workspaceIdentity, CapabilityLifecyclePreviewRequest request, string requestHash, long baselineCatalogRevision, long baselineActivationRevision, string? targetDescriptorJson, string? targetArtifactDigest, long lifecycleRevision, long dependentRevision, string dependentHash, IEnumerable<CapabilityLifecycleImpact> impacts)
    {
        var builder = new StringBuilder($"capability-lifecycle-preview-v1\n{workspaceIdentity}\n{requestHash}\n{baselineCatalogRevision}\n{baselineActivationRevision}\n{targetDescriptorJson}\n{targetArtifactDigest}\n{lifecycleRevision}\n{dependentRevision}\n{dependentHash}\n");
        foreach (var impact in impacts)
        {
            builder.Append((int)impact.DependentKind).Append('\n').Append(impact.DependentIdentity).Append('\n').Append(impact.DependentRevision).Append('\n').Append((int)impact.RequirementKind).Append('\n').Append(impact.CompatibleVersionRange).Append('\n').Append(impact.IsCompatible ? '1' : '0').Append('\n').Append((int)impact.AuthorityPosture).Append('\n').Append((int)impact.Outcome).Append('\n');
        }
        return CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(builder.ToString())).Value;
    }

    private static CapabilityLifecycleDocument Empty(string workspaceIdentity)
    {
        var document = new CapabilityLifecycleDocument(CapabilityLifecycleDocument.CurrentSchemaVersion, workspaceIdentity, 0, 0, string.Empty, [], [], [], string.Empty, string.Empty);
        return Seal(document);
    }

    private static CapabilityLifecycleDocument Seal(CapabilityLifecycleDocument document)
    {
        var content = JsonSerializer.Serialize(document with { ContentDigest = string.Empty, AuthenticationTag = string.Empty }, _hashOptions);
        return document with { ContentDigest = CapabilityIntegrityDigest.Compute(Encoding.UTF8.GetBytes(content)).Value, AuthenticationTag = string.Empty };
    }

    private static bool IsValid(CapabilityLifecycleDocument document, string workspaceIdentity)
    {
        if (document.SchemaVersion != CapabilityLifecycleDocument.CurrentSchemaVersion || document.WorkspaceIdentity != workspaceIdentity || document.Generation < 0 || document.DependentSetRevision < 0 || document.DependentSetHash is null || document.Dependents is null || document.Entries is null || document.Operations is null || document.Dependents.Count > MaximumDependents || document.Entries.Count > MaximumEntries || document.Operations.Count > MaximumOperations || !IsDigest(document.ContentDigest) || document.ContentDigest != Seal(document).ContentDigest)
        {
            return false;
        }
        if (document.Generation == 0 && (document.DependentSetRevision != 0 || document.DependentSetHash.Length != 0 || document.Dependents.Count != 0 || document.Entries.Count != 0 || document.Operations.Count != 0))
        {
            return false;
        }
        if (!document.Dependents.All(IsValidDependent) || !document.Entries.All(IsValidEntry) || !document.Operations.All(operation => IsValidOperation(operation, document.Generation, document.DependentSetRevision)))
        {
            return false;
        }
        if (document.Generation > 0 && (!IsDigest(document.DependentSetHash) || document.DependentSetHash != ComputeDependentSetHash(document.Dependents)))
        {
            return false;
        }
        return document.Entries.Select(entry => entry.CapabilityId).Distinct(StringComparer.Ordinal).Count() == document.Entries.Count && document.Dependents.Select(entry => $"{(int)entry.Kind}:{entry.Identity}").Distinct(StringComparer.Ordinal).Count() == document.Dependents.Count && document.Operations.Select(operation => operation.OperationId).Distinct(StringComparer.Ordinal).Count() == document.Operations.Count;
    }

    private static bool IsValidDependent(CapabilityDependentDocument? dependent) => dependent is not null && Enum.IsDefined(dependent.Kind) && Enum.IsDefined(dependent.AuthorityPosture) && IsSafeText(dependent.Identity, 256) && IsSafeText(dependent.Revision, 256) && CapabilityDependencyManifestValidator.Validate(dependent.Manifest).IsValid;

    private static bool IsValidSnapshot(CapabilityDependentIndexSnapshot snapshot)
    {
        if (snapshot.Status != CapabilityDependentIndexStatus.Available || !IsDigest(snapshot.Hash) || snapshot.Dependents.Count > MaximumDependents || snapshot.Dependents.Any(dependent => !IsValidDependent(dependent)))
        {
            return false;
        }
        var identities = snapshot.Dependents.Select(dependent => $"{(int)dependent.Kind}:{dependent.Identity}").ToArray();
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length || !identities.SequenceEqual(snapshot.Dependents.OrderBy(dependent => dependent.Kind).ThenBy(dependent => dependent.Identity, StringComparer.Ordinal).Select(dependent => $"{(int)dependent.Kind}:{dependent.Identity}")))
        {
            return false;
        }
        return snapshot.Hash == ComputeDependentSetHash(snapshot.Dependents.Select(Map));
    }

    private static bool IsValidDependent(CapabilityDependent? dependent) => dependent is not null && Enum.IsDefined(dependent.Kind) && Enum.IsDefined(dependent.AuthorityPosture) && IsSafeText(dependent.Identity, 256) && IsSafeText(dependent.Revision, 256) && CapabilityDependencyManifestValidator.Validate(dependent.Manifest).IsValid;

    private static bool IsValidEntry(CapabilityLifecycleEntryDocument? entry)
    {
        if (entry is null || !CapabilityId.TryParse(entry.CapabilityId, out _, out _) || !IsCanonicalDescriptor(entry.DescriptorJson, entry.CapabilityId) || !IsDigest(entry.ArtifactDigest) || entry.Revision < 0 || !CapabilityArtifactManifestValidator.IsOperationId(entry.LastOperationId) || entry.UpdatedAtUtc.Offset != TimeSpan.Zero || entry.BaselineCatalogRevision < 0 || entry.BaselineActivationRevision < 1 || entry.History is null || entry.Degradations is null || entry.History.Count > MaximumHistoryPerEntry)
        {
            return false;
        }
        if (entry.IsRemoved && entry.IsEnabled || entry.History.Any(history => !IsValidHistory(history, entry.CapabilityId) || history.Revision >= entry.Revision) || entry.History.Zip(entry.History.Skip(1)).Any(pair => pair.First.Revision >= pair.Second.Revision))
        {
            return false;
        }
        return entry.Degradations.All(degradation => IsValidDegradation(degradation, entry.CapabilityId));
    }

    private static bool IsValidHistory(CapabilityLifecycleHistoryDocument? history, string capabilityId) => history is not null && IsCanonicalDescriptor(history.DescriptorJson, capabilityId) && IsDigest(history.ArtifactDigest) && history.Revision >= 0 && CapabilityArtifactManifestValidator.IsOperationId(history.OperationId) && history.ChangedAtUtc.Offset == TimeSpan.Zero && (!history.IsRemoved || !history.IsEnabled);

    private static bool IsValidDegradation(CapabilityLifecycleDegradationDocument? degradation, string capabilityId) => degradation is not null && degradation.CapabilityId == capabilityId && CapabilityArtifactManifestValidator.IsOperationId(degradation.OperationId) && Enum.TryParse<CapabilityDependentKind>(degradation.DependentKind, out var kind) && Enum.IsDefined(kind) && IsSafeText(degradation.DependentIdentity, 256) && IsSafeText(degradation.DependentRevision, 256) && CapabilityVersionRange.TryParse(degradation.CompatibleVersionRange, out _, out _) && degradation.RecordedAtUtc.Offset == TimeSpan.Zero;

    private static bool IsValidOperation(CapabilityLifecycleOperationDocument? operation, long generation, long dependentSetRevision)
    {
        if (operation is null || !CapabilityArtifactManifestValidator.IsOperationId(operation.OperationId) || !IsDigest(operation.RequestHash) || !Enum.IsDefined(operation.Kind) || !CapabilityId.TryParse(operation.CapabilityId, out _, out _) || operation.BaselineCatalogRevision < 0 || operation.BaselineActivationRevision < 1 || operation.PreviewRevision < 1 || operation.PreviewRevision > generation || operation.DependentSetRevision < 0 || operation.DependentSetRevision > dependentSetRevision || !IsDigest(operation.DependentSetHash) || !IsDigest(operation.PreviewHash) || operation.Impacts is null || operation.Impacts.Any(impact => !IsValidImpact(impact)))
        {
            return false;
        }
        if (operation.Kind is CapabilityLifecycleOperationKind.Upgrade or CapabilityLifecycleOperationKind.Rollback ? !IsCanonicalDescriptor(operation.TargetDescriptorJson, operation.CapabilityId) || !IsDigest(operation.TargetArtifactDigest) : operation.TargetDescriptorJson is not null || operation.TargetArtifactDigest is not null)
        {
            return false;
        }
        if (operation.Outcome is null)
        {
            return operation.ResultRevision is null && !operation.OutcomeAuditPending;
        }
        return (operation.Outcome is CapabilityLifecycleMutationStatus.Applied or CapabilityLifecycleMutationStatus.Conflict or CapabilityLifecycleMutationStatus.Blocked or CapabilityLifecycleMutationStatus.NotFound) && operation.ResultRevision > operation.PreviewRevision && operation.ResultRevision <= generation;
    }

    private static bool IsValidImpact(CapabilityLifecycleImpact? impact)
    {
        if (impact is null || !Enum.IsDefined(impact.DependentKind) || !Enum.IsDefined(impact.RequirementKind) || !Enum.IsDefined(impact.AuthorityPosture) || !Enum.IsDefined(impact.Outcome) || !IsSafeText(impact.DependentIdentity, 256) || !IsSafeText(impact.DependentRevision, 256) || !CapabilityVersionRange.TryParse(impact.CompatibleVersionRange, out _, out _))
        {
            return false;
        }
        return impact.IsCompatible ? impact.Outcome == CapabilityLifecycleImpactOutcome.Preserved : impact.RequirementKind == CapabilityRequirementKind.Required ? impact.Outcome == CapabilityLifecycleImpactOutcome.Blocked : impact.Outcome == CapabilityLifecycleImpactOutcome.Degraded;
    }

    private static bool IsCanonicalDescriptor(string? descriptorJson, string capabilityId) => TryParseDescriptor(descriptorJson, out var descriptor) && descriptor is not null && descriptor.Id.Value == capabilityId && CapabilityDescriptorJson.TrySerialize(descriptor, out var canonical, out _) && canonical == descriptorJson;

    private static string ComputeDependentSetHash(IEnumerable<CapabilityDependentDocument> dependents)
    {
        var builder = new StringBuilder("capability-dependent-index-v1\n");
        foreach (var dependent in dependents)
        {
            _ = CapabilityDependencyManifestHash.TryCompute(dependent.Manifest, out var manifestHash, out _);
            AppendDependentHashValue(builder, ((int)dependent.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendDependentHashValue(builder, dependent.Identity);
            AppendDependentHashValue(builder, dependent.Revision);
            AppendDependentHashValue(builder, ((int)dependent.AuthorityPosture).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendDependentHashValue(builder, manifestHash!.Value);
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static void AppendDependentHashValue(StringBuilder builder, string value) => builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');

    private static bool IsSafeText(string? value, int maximum) => value is not null && value.Length is > 0 && value.Length <= maximum && value.All(character => character >= (char)0x20 && character != (char)0x7f);

    private static string TrustIdentity(string workspaceIdentity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("embodysense-capability-lifecycle-v1\n" + workspaceIdentity));
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static CapabilityDependentDocument Map(CapabilityDependent dependent) => new(dependent.Kind, dependent.Identity, dependent.Revision, dependent.Manifest, dependent.AuthorityPosture);

    private static CapabilityLifecycleState? Current(CapabilityLifecycleDocument document, string capabilityId) => document.Entries.SingleOrDefault(entry => entry.CapabilityId == capabilityId) is { } entry ? Map(entry) : null;

    private static CapabilityLifecycleState Map(CapabilityLifecycleEntryDocument entry)
    {
        if (!TryParseDescriptor(entry.DescriptorJson, out var descriptor) || !CapabilityIntegrityDigest.TryParse(entry.ArtifactDigest, out var digest, out _))
        {
            throw new FormatException("The lifecycle entry contains malformed descriptor or artifact evidence.");
        }
        return new CapabilityLifecycleState(descriptor!, digest!, entry.IsEnabled, entry.IsRemoved, entry.Revision, entry.LastOperationId, entry.UpdatedAtUtc);
    }

    private static IReadOnlyList<CapabilityLifecycleOperationDocument> Replace(IEnumerable<CapabilityLifecycleOperationDocument> operations, CapabilityLifecycleOperationDocument replacement) => operations.Select(operation => operation.OperationId == replacement.OperationId ? replacement : operation).ToArray();

    private static CapabilityLifecyclePreview MapPreview(string workspaceIdentity, CapabilityLifecycleOperationDocument operation, CapabilityLifecyclePreviewStatus status, string detail)
    {
        _ = CapabilityId.TryParse(operation.CapabilityId, out var capabilityId, out _);
        _ = TryParseDescriptor(operation.TargetDescriptorJson, out var targetDescriptor);
        _ = CapabilityIntegrityDigest.TryParse(operation.TargetArtifactDigest, out var targetArtifactDigest, out _);
        return new CapabilityLifecyclePreview(status, workspaceIdentity, operation.OperationId, operation.Kind, capabilityId!, operation.PreviewRevision, operation.DependentSetRevision, operation.DependentSetHash, operation.PreviewHash, operation.Impacts, detail, operation.BaselineCatalogRevision, operation.BaselineActivationRevision, targetDescriptor, targetArtifactDigest);
    }

    private static CapabilityLifecyclePreview PreviewResult(CapabilityLifecyclePreviewStatus status, CapabilityLifecyclePreviewRequest request, string detail, string workspaceIdentity = "unavailable") => new(status, workspaceIdentity, request.OperationId ?? string.Empty, request.Kind, request.CapabilityId, 0, 0, string.Empty, string.Empty, [], detail);

    private static bool SameBaseline(CapabilityLifecycleBaseline? first, CapabilityLifecycleBaseline? second) => first is not null && second is not null && first.CatalogRevision == second.CatalogRevision && first.ActivationRevision == second.ActivationRevision;

    private static bool MatchesBaseline(CapabilityLifecycleEntryDocument entry, CapabilityLifecycleBaseline? baseline) => baseline is not null && entry.BaselineCatalogRevision == baseline.CatalogRevision && entry.BaselineActivationRevision == baseline.ActivationRevision;

    private static CapabilityLifecycleMutationResult Result(CapabilityLifecycleMutationStatus status, CapabilityLifecycleState? state, long? revision, bool pending, string detail, CapabilityLifecycleMutationStatus? replayedOutcome = null) => new(status, state, revision, pending, detail, replayedOutcome);

    private static string? SerializeDescriptor(CapabilityDescriptor? descriptor) => descriptor is not null && CapabilityDescriptorJson.TrySerialize(descriptor, out var json, out _) ? json : null;

    private static bool TryParseDescriptor(string? json, out CapabilityDescriptor? descriptor) => CapabilityDescriptorJson.TryDeserialize(json, out descriptor, out _);

    private static bool IsDigest(string? value) => CapabilityIntegrityDigest.TryParse(value, out _, out _);

    private static bool MatchesCurrent(CapabilityLifecycleDocument document, CapabilityCatalogTrustState trust) => document.Generation == trust.CurrentGeneration && document.ContentDigest == trust.CurrentContentDigest;

    private static bool MatchesPrevious(CapabilityLifecycleDocument document, CapabilityCatalogTrustState trust) => trust.PreviousGeneration == document.Generation && trust.PreviousContentDigest == document.ContentDigest;

    private static CapabilityLifecycleReadResult ReadResult(CapabilityLifecycleReadStatus status, CapabilityLifecycleDocument? document, CapabilityLifecycleEntryDocument? entry, string detail)
    {
        if (entry is null)
        {
            return new CapabilityLifecycleReadResult(status, null, [], [], document?.Generation, detail);
        }
        var history = entry.History.Select(item =>
        {
            if (!TryParseDescriptor(item.DescriptorJson, out var descriptor) || !CapabilityIntegrityDigest.TryParse(item.ArtifactDigest, out var digest, out _))
            {
                throw new FormatException("Lifecycle history contains malformed descriptor or artifact evidence.");
            }
            return new CapabilityLifecycleHistoryEntry(descriptor!, digest!, item.IsEnabled, item.IsRemoved, item.Revision, item.OperationId, item.ChangedAtUtc);
        }).ToArray();
        var degradations = entry.Degradations.Select(item => Enum.TryParse<CapabilityDependentKind>(item.DependentKind, out var kind) ? new CapabilityLifecycleDegradation(item.OperationId, kind, item.DependentIdentity, item.DependentRevision, item.CompatibleVersionRange, item.RecordedAtUtc) : throw new FormatException("Lifecycle degradation evidence contains an unsupported dependent kind.")).ToArray();
        return new CapabilityLifecycleReadResult(status, Map(entry), history, degradations, document!.Generation, detail);
    }

    private static bool IsAvailabilityFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or FormatException or JsonException or OverflowException;

    private static void RejectDuplicateMembers(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("Lifecycle persistence JSON contains duplicate members.");
                }
                RejectDuplicateMembers(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateMembers(item);
            }
        }
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented) => new(JsonSerializerDefaults.Web) { WriteIndented = writeIndented, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, Converters = { new CapabilityScalarJsonConverterFactory(), new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) } };
}
