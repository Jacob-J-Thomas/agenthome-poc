using EmbodySense.Core.Application.LocalWorkspace.Actions;
using EmbodySense.Core.Application.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Authenticates private workspace artifacts against the canonical effect-attempt store.</summary>
public sealed class WorkspaceActionAttemptStorePresenceResolver : IWorkspaceActionAttemptPresenceResolver
{
    private readonly WorkspaceActionEvidenceStore _evidence;
    private readonly GovernedLoopEffectAttemptStore _store;

    /// <summary>Creates one presence and cleanup adapter over the canonical attempt and evidence stores.</summary>
    public WorkspaceActionAttemptStorePresenceResolver(
        EmbodySense.Core.Common.Workspace.WorkspacePaths paths,
        WorkspaceActionEvidenceStore? evidenceStore = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _store = new GovernedLoopEffectAttemptStore(paths);
        _evidence = evidenceStore ?? new WorkspaceActionEvidenceStore(paths);
    }

    /// <inheritdoc />
    public async Task<WorkspaceActionAttemptPresence> ResolveAsync(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        string beforeEvidenceId,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(effectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(idempotencyOperationId)
            || effectGeneration < 1
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(beforeEvidenceId))
        {
            return WorkspaceActionAttemptPresence.Unknown;
        }

        var resumed = await _store.ResumeAsync(idempotencyOperationId, effectGeneration, cancellationToken).ConfigureAwait(false);
        using var lease = resumed.Lease;
        if (resumed.Status == GovernedLoopEffectAttemptStoreStatus.NotFound)
        {
            return WorkspaceActionAttemptPresence.NotFound;
        }
        if (resumed.Status is not (GovernedLoopEffectAttemptStoreStatus.Replayed or GovernedLoopEffectAttemptStoreStatus.OperationInProgress)
            || resumed.Attempt is null
            || !string.Equals(resumed.Attempt.Payload.EffectId, effectId, StringComparison.Ordinal)
            || !string.Equals(resumed.Attempt.Payload.OperationId, idempotencyOperationId, StringComparison.Ordinal)
            || resumed.Attempt.Payload.EffectGeneration != effectGeneration
            || !string.Equals(resumed.Attempt.BeforeEvidenceId, beforeEvidenceId, StringComparison.Ordinal))
        {
            return WorkspaceActionAttemptPresence.Unknown;
        }
        return resumed.Attempt.Payload.Phase == GovernedLoopEffectPhase.DispatchNotStarted
            || resumed.Attempt.Payload.Phase is GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.Committed
                && resumed.Attempt.AfterEvidenceId is not null
                && resumed.Attempt.Payload.OutcomeEvidenceId is not null
            ? WorkspaceActionAttemptPresence.ArtifactReleased
            : WorkspaceActionAttemptPresence.Exists;
    }

    /// <inheritdoc />
    public async Task<WorkspaceActionPreparationCleanupResult> TryCleanupPreparationsAsync(
        IReadOnlyList<WorkspaceActionBeforeEvidence> beforeEvidence,
        int maximumRemovals,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(beforeEvidence);
        if (beforeEvidence.Count > WorkspaceActionContractLimits.MaxEvidenceRecordsPerKind
            || maximumRemovals is < 1 or > 64
            || beforeEvidence.Any(candidate => candidate is null || WorkspaceActionEvidenceContract.ValidateBefore(candidate) is not null)
            || beforeEvidence.Select(candidate => candidate.EvidenceId).Distinct(StringComparer.Ordinal).Count() != beforeEvidence.Count)
        {
            return WorkspaceActionPreparationCleanupResult.Unknown;
        }
        var byId = beforeEvidence.ToDictionary(candidate => candidate.EvidenceId, StringComparer.Ordinal);
        var result = await _store.TryCleanupUnreferencedBeforeEvidenceAsync(
            beforeEvidence.Select(candidate => candidate.EvidenceId).ToArray(),
            maximumRemovals,
            (evidenceId, token) => _evidence.DeleteExactBeforeAsync(byId[evidenceId], token),
            cancellationToken).ConfigureAwait(false);
        return new WorkspaceActionPreparationCleanupResult(result.EvidenceComplete, result.RemovedCount);
    }
}
