using EmbodySense.Core.Application.LocalWorkspace.Actions;
using EmbodySense.Core.Application.Loops.EffectAttempts.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.EffectAttempts;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Proves a governed-version predecessor against both workspace outcome evidence and the canonical committed effect head.</summary>
public sealed class WorkspaceActionCommittedAfterEvidenceStoreResolver : IWorkspaceActionCommittedAfterEvidenceResolver
{
    private readonly WorkspaceActionEvidenceStore _evidence;
    private readonly WorkspacePaths _paths;

    /// <summary>Creates one read-only resolver over the canonical workspace and effect-attempt stores.</summary>
    public WorkspaceActionCommittedAfterEvidenceStoreResolver(
        WorkspacePaths paths,
        WorkspaceActionEvidenceStore? evidenceStore = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _evidence = evidenceStore ?? new WorkspaceActionEvidenceStore(paths);
    }

    /// <inheritdoc />
    public async Task<bool> IsCommittedAsync(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        string afterEvidenceId,
        string afterEvidenceHash,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(effectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(idempotencyOperationId)
            || effectGeneration < 1
            || !afterEvidenceId.StartsWith("after-", StringComparison.Ordinal)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(afterEvidenceId["after-".Length..])
            || !WorkspaceActionFingerprint.IsCanonicalSha256(afterEvidenceHash))
        {
            return false;
        }

        try
        {
            var after = await _evidence.ReadAfterAsync(afterEvidenceId, cancellationToken).ConfigureAwait(false);
            var outcome = await _evidence.FindOutcomeAsync(effectId, idempotencyOperationId, effectGeneration, cancellationToken).ConfigureAwait(false);
            if (WorkspaceActionEvidenceContract.ValidateAfter(after) is not null
                || WorkspaceActionEvidenceContract.ValidateOutcome(outcome) is not null
                || !string.Equals(after!.ContentHashOfRecord, afterEvidenceHash, StringComparison.Ordinal)
                || !string.Equals(after.EffectId, effectId, StringComparison.Ordinal)
                || !string.Equals(after.IdempotencyOperationId, idempotencyOperationId, StringComparison.Ordinal)
                || after.EffectGeneration != effectGeneration
                || !string.Equals(outcome!.AfterEvidenceId, after.EvidenceId, StringComparison.Ordinal)
                || !string.Equals(outcome.AfterEvidenceHash, after.ContentHashOfRecord, StringComparison.Ordinal)
                || !string.Equals(outcome.BeforeEvidenceId, after.BeforeEvidenceId, StringComparison.Ordinal)
                || !string.Equals(outcome.OperationId, after.OperationId, StringComparison.Ordinal)
                || !string.Equals(outcome.TargetFingerprint, after.TargetFingerprint, StringComparison.Ordinal)
                || outcome.GovernedVersion != after.GovernedVersion
                || outcome.ObservedAtUtc != after.ObservedAtUtc)
            {
                return false;
            }

            var resolved = await new GovernedLoopEffectAttemptStore(_paths).ResumeAsync(
                idempotencyOperationId,
                effectGeneration,
                cancellationToken).ConfigureAwait(false);
            using var lease = resolved.Lease;
            var attempt = resolved.Attempt;
            return resolved.Status == GovernedLoopEffectAttemptStoreStatus.Replayed
                && lease is null
                && GovernedLoopEffectAttemptContract.Validate(attempt) is null
                && attempt!.Payload.Phase == GovernedLoopEffectPhase.Committed
                && attempt.Payload.Outcome == GovernedLoopEffectOutcome.Succeeded
                && string.Equals(attempt.Payload.EffectId, effectId, StringComparison.Ordinal)
                && string.Equals(attempt.Payload.OperationId, idempotencyOperationId, StringComparison.Ordinal)
                && attempt.Payload.EffectGeneration == effectGeneration
                && string.Equals(attempt.ActuatorOperationId, after.OperationId, StringComparison.Ordinal)
                && string.Equals(attempt.TargetFingerprint, after.TargetFingerprint, StringComparison.Ordinal)
                && string.Equals(attempt.BeforeEvidenceId, after.BeforeEvidenceId, StringComparison.Ordinal)
                && string.Equals(attempt.AfterEvidenceId, after.EvidenceId, StringComparison.Ordinal)
                && string.Equals(attempt.Payload.OutcomeEvidenceId, outcome.EvidenceId, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
