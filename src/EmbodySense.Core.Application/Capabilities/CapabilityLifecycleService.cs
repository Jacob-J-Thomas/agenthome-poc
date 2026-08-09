using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Orchestrates audited preview and conflict-safe capability lifecycle mutation across every registered dependent.</summary>
public sealed class CapabilityLifecycleService
{
    private readonly ICapabilityDependentIndex _dependentIndex;
    private readonly ICapabilityLifecycleBaselineSource _baselineSource;
    private readonly ICapabilityLifecycleArtifactEvidenceSource _artifactEvidence;
    private readonly ICapabilityLifecycleMutationStore _store;
    private readonly IAuditLog _auditLog;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>Creates the lifecycle orchestrator over explicit dependent, baseline, artifact, persistence, and audit ports.</summary>
    /// <remarks>Artifact proof is enforced by <paramref name="store"/> inside its authenticated operation lookup so persisted previews remain recoverable after artifact loss.</remarks>
    public CapabilityLifecycleService(ICapabilityDependentIndex dependentIndex, ICapabilityLifecycleBaselineSource baselineSource, ICapabilityLifecycleArtifactEvidenceSource artifactEvidence, ICapabilityLifecycleMutationStore store, IAuditLog auditLog, ICapabilityAuthorityTransaction authorityTransaction)
    {
        ArgumentNullException.ThrowIfNull(dependentIndex);
        ArgumentNullException.ThrowIfNull(baselineSource);
        ArgumentNullException.ThrowIfNull(artifactEvidence);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(auditLog);
        ArgumentNullException.ThrowIfNull(authorityTransaction);
        _dependentIndex = dependentIndex;
        _baselineSource = baselineSource;
        _artifactEvidence = artifactEvidence;
        _store = store;
        _auditLog = auditLog;
        _authorityTransaction = authorityTransaction;
    }

    /// <summary>Captures, persists, and audits one deterministic lifecycle impact preview.</summary>
    public async Task<CapabilityLifecyclePreview> PreviewAsync(CapabilityLifecyclePreviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capabilityId = request.CapabilityId;
        var auditTarget = capabilityId?.Value ?? "invalid";
        await AppendAsync(AuditSchema.Actions.CapabilityLifecycleIntent, auditTarget, AuditSchema.Outcomes.Started, request.OperationId, request.Kind, null, "Capability lifecycle preview was requested.");
        if (capabilityId is null)
        {
            var rejected = new CapabilityLifecyclePreview(CapabilityLifecyclePreviewStatus.Invalid, "unavailable", request.OperationId, request.Kind, capabilityId!, 0, 0, string.Empty, string.Empty, [], "The lifecycle capability target is invalid.");
            await AppendAsync(AuditSchema.Actions.CapabilityLifecyclePreview, auditTarget, AuditSchema.Outcomes.Failed, request.OperationId, request.Kind, rejected, rejected.Detail);
            return rejected;
        }
        var preview = await _authorityTransaction.ExecuteAsync(async transactionCancellationToken =>
        {
            var dependents = await _dependentIndex.CaptureAsync(transactionCancellationToken);
            var baseline = await _baselineSource.ReadAsync(capabilityId, transactionCancellationToken);
            dependents = await FinalizeDependentsAsync(dependents, transactionCancellationToken);
            return await _store.PreviewAsync(request, baseline, dependents, transactionCancellationToken);
        }, cancellationToken);
        var outcome = preview.Status is CapabilityLifecyclePreviewStatus.Ready or CapabilityLifecyclePreviewStatus.Replayed ? AuditSchema.Outcomes.Succeeded : preview.Status == CapabilityLifecyclePreviewStatus.Conflict ? AuditSchema.Outcomes.Conflict : AuditSchema.Outcomes.Failed;
        await AppendAsync(AuditSchema.Actions.CapabilityLifecyclePreview, auditTarget, outcome, request.OperationId, request.Kind, preview, preview.Detail);
        return preview;
    }

    /// <summary>Returns an exact persisted selection preview without consulting current staged artifact evidence.</summary>
    /// <remarks>A missing operation is returned as <see cref="CapabilityLifecyclePreviewStatus.NotFound"/> so the caller can resolve and validate a new operation.</remarks>
    public async Task<CapabilityLifecyclePreview> TryReplaySelectionAsync(CapabilityLifecycleSelectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preview = await _authorityTransaction.ExecuteAsync(transactionCancellationToken => _store.TryReplaySelectionAsync(request, transactionCancellationToken), cancellationToken);
        if (preview.Status == CapabilityLifecyclePreviewStatus.NotFound)
        {
            return preview;
        }

        var auditTarget = request.CapabilityId?.Value ?? "invalid";
        await AppendAsync(AuditSchema.Actions.CapabilityLifecycleIntent, auditTarget, AuditSchema.Outcomes.Started, request.OperationId, request.Kind, null, "A persisted lifecycle selection replay was requested.");
        var outcome = preview.Status == CapabilityLifecyclePreviewStatus.Replayed ? AuditSchema.Outcomes.Succeeded : preview.Status == CapabilityLifecyclePreviewStatus.Conflict ? AuditSchema.Outcomes.Conflict : AuditSchema.Outcomes.Failed;
        await AppendAsync(AuditSchema.Actions.CapabilityLifecyclePreview, auditTarget, outcome, request.OperationId, request.Kind, preview, preview.Detail);
        return preview;
    }

    internal Task<CapabilityLifecycleTargetResolution> ResolveCurrentEnableTargetAsync(CapabilityLifecycleTargetResolutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CapabilityId is null || request.Kind != CapabilityLifecycleOperationKind.Enable)
        {
            return Task.FromResult(new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "Only enable may resolve the current proved lifecycle target."));
        }

        return _authorityTransaction.ExecuteAsync(async transactionCancellationToken =>
        {
            var current = await _store.ReadAsync(request.CapabilityId, transactionCancellationToken);
            if (current.Status == CapabilityLifecycleReadStatus.NotFound)
            {
                return new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.NotFound, null, null, "No current lifecycle entry is registered for this capability.");
            }
            if (current.Status != CapabilityLifecycleReadStatus.Available || current.State is null)
            {
                return new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "The current authenticated lifecycle entry is unavailable.");
            }
            if (!current.State.Descriptor.Id.Equals(request.CapabilityId))
            {
                return new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, "The current lifecycle entry does not match the selected capability identity.");
            }
            if (request.TargetVersion is not null && !current.State.Descriptor.Version.Equals(request.TargetVersion))
            {
                return new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.NotFound, null, null, "The selected version is not the current proved lifecycle descriptor.");
            }

            var evidence = await _artifactEvidence.VerifyAsync(current.State.Descriptor, current.State.ArtifactDigest, transactionCancellationToken);
            return evidence.Status switch
            {
                CapabilityLifecycleArtifactEvidenceStatus.Proved => new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Available, current.State.Descriptor, current.State.ArtifactDigest, evidence.Detail),
                CapabilityLifecycleArtifactEvidenceStatus.NotFound => new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.NotFound, null, null, evidence.Detail),
                _ => new CapabilityLifecycleTargetResolution(CapabilityLifecycleTargetResolutionStatus.Unavailable, null, null, evidence.Detail)
            };
        }, cancellationToken);
    }

    /// <summary>Recaptures every dependent and atomically applies or rejects the exact audited preview.</summary>
    public async Task<CapabilityLifecycleMutationResult> MutateAsync(CapabilityLifecyclePreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var auditTarget = preview.CapabilityId?.Value ?? "invalid";
        var result = await _authorityTransaction.ExecuteAsync(async transactionCancellationToken =>
        {
            var dependents = await _dependentIndex.CaptureAsync(transactionCancellationToken);
            var baseline = preview.CapabilityId is null ? null : await _baselineSource.ReadAsync(preview.CapabilityId, transactionCancellationToken);
            dependents = await FinalizeDependentsAsync(dependents, transactionCancellationToken);
            return await _store.MutateAsync(preview, baseline, dependents, transactionCancellationToken);
        }, cancellationToken);
        var terminalStatus = result.ReplayedOutcome ?? (result.Status == CapabilityLifecycleMutationStatus.Replayed ? CapabilityLifecycleMutationStatus.Applied : result.Status);
        var action = preview.Kind == CapabilityLifecycleOperationKind.Rollback ? AuditSchema.Actions.CapabilityLifecycleRollback : terminalStatus == CapabilityLifecycleMutationStatus.Conflict ? AuditSchema.Actions.CapabilityLifecycleConflict : AuditSchema.Actions.CapabilityLifecycleMutation;
        var outcome = terminalStatus == CapabilityLifecycleMutationStatus.Applied ? AuditSchema.Outcomes.Succeeded : terminalStatus == CapabilityLifecycleMutationStatus.Conflict ? AuditSchema.Outcomes.Conflict : terminalStatus == CapabilityLifecycleMutationStatus.Blocked ? AuditSchema.Outcomes.Denied : AuditSchema.Outcomes.Failed;
        await AppendAsync(action, auditTarget, outcome, preview.OperationId, preview.Kind, preview, result.Detail);
        if (result.OutcomeAuditPending)
        {
            await AppendAsync(AuditSchema.Actions.CapabilityLifecycleFinal, auditTarget, outcome, preview.OperationId, preview.Kind, preview, result.Detail);
            var auditMark = await _store.MarkOutcomeAuditedAsync(preview.OperationId, CancellationToken.None);
            if (auditMark is CapabilityLifecycleAuditMarkStatus.Applied or CapabilityLifecycleAuditMarkStatus.NoChange)
            {
                result = result with { OutcomeAuditPending = false };
            }
        }
        return result;
    }

    private async Task<CapabilityDependentIndexSnapshot> FinalizeDependentsAsync(CapabilityDependentIndexSnapshot initial, CancellationToken cancellationToken)
    {
        if (initial.Status != CapabilityDependentIndexStatus.Available)
        {
            return initial;
        }

        var finalized = await _dependentIndex.CaptureAsync(cancellationToken);
        return finalized.Status == CapabilityDependentIndexStatus.Available && string.Equals(initial.Hash, finalized.Hash, StringComparison.Ordinal)
            ? finalized
            : new CapabilityDependentIndexSnapshot(CapabilityDependentIndexStatus.Unavailable, string.Empty, [], "The registered dependent set changed or became unavailable during capability authority finalization.");
    }

    private Task AppendAsync(string action, string target, string outcome, string operationId, CapabilityLifecycleOperationKind kind, CapabilityLifecyclePreview? preview, string detail)
    {
        var metadata = new Dictionary<string, object?> { ["operationId"] = operationId, ["transition"] = kind.ToString(), ["lifecycleRevision"] = preview?.LifecycleRevision, ["baselineCatalogRevision"] = preview?.BaselineCatalogRevision, ["baselineActivationRevision"] = preview?.BaselineActivationRevision, ["dependentSetRevision"] = preview?.DependentSetRevision, ["dependentSetHash"] = preview?.DependentSetHash, ["previewHash"] = preview?.PreviewHash };
        return _auditLog.AppendAsync(AuditEvent.Create(AuditSchema.Actors.CapabilityHost, action, target, outcome, detail.Length <= 512 ? detail : detail[..512], metadata), CancellationToken.None);
    }
}
