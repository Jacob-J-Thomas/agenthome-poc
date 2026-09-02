using System.Security.Cryptography;
using System.Text;
using EmbodySense.Core.Common.Loops.Execution.Effects;
using EmbodySense.Core.Common.Loops.Execution.Effects.Models;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation;

/// <summary>Creates only proof-backed typed reconciled successors without widening generic dispatch or replay.</summary>
public static class GovernedLoopEffectReconciliationAttemptContract
{
    /// <summary>Gets whether the exact case may create a typed reconciled successor for the current attempt.</summary>
    public static bool CanCreateSuccessor(GovernedLoopEffectAttempt? currentAttempt, GovernedLoopEffectReconciliationCase? reconciliationCase)
    {
        if (currentAttempt is null || reconciliationCase is null || !GovernedLoopEffectReconciliationContractValidator.Validate(reconciliationCase, currentAttempt).IsValid)
        {
            return false;
        }

        return reconciliationCase.Resolution is not null
            && reconciliationCase.Disposition is not null
            && reconciliationCase.Disposition.Kind is GovernedLoopEffectReconciliationDispositionKind.AcceptProvedNotApplied or GovernedLoopEffectReconciliationDispositionKind.AcceptProvedApplied;
    }

    /// <summary>Creates the sole typed reconciled successor authorized by the exact accepted assessment, disposition, and resolution.</summary>
    /// <exception cref="InvalidOperationException">Thrown when proof is unresolved, quarantined, stale, mismatched, or otherwise invalid.</exception>
    public static GovernedLoopEffectAttempt CreateSuccessor(GovernedLoopEffectAttempt currentAttempt, GovernedLoopEffectReconciliationCase reconciliationCase)
    {
        ArgumentNullException.ThrowIfNull(currentAttempt);
        ArgumentNullException.ThrowIfNull(reconciliationCase);
        if (!CanCreateSuccessor(currentAttempt, reconciliationCase))
        {
            throw new InvalidOperationException("Only exact accepted proof of not-applied or applied-with-known-outcome may create a reconciled effect successor.");
        }

        var resolution = reconciliationCase.Resolution!;
        var payload = GovernedLoopEffectPayload.Create(
            currentAttempt.Payload.SchemaVersion,
            currentAttempt.Payload.EffectId,
            currentAttempt.Payload.OperationId,
            currentAttempt.Payload.EffectGeneration,
            currentAttempt.Payload.Origin,
            currentAttempt.Payload.OriginNodeId,
            currentAttempt.Payload.IntentHash,
            GovernedLoopEffectPhase.Reconciled,
            resolution.Outcome,
            GovernedLoopEffectEvidenceStatus.Complete,
            resolution.OutcomeEvidenceId,
            resolution.ResolutionId,
            resolution.ResolvedAtUtc);
        var successor = currentAttempt with
        {
            AfterEvidenceId = currentAttempt.AfterEvidenceId ?? resolution.OutcomeEvidenceId,
            Payload = payload,
            PreviousContentHash = currentAttempt.ContentHash,
            ContentHash = string.Empty
        };
        successor = successor with { ContentHash = GovernedLoopEffectAttemptContract.Compute(successor) };
        if (!IsDirectSuccessor(currentAttempt, successor, reconciliationCase))
        {
            throw new InvalidOperationException("The proof-backed reconciliation did not produce an exact immutable attempt successor.");
        }
        return successor;
    }

    /// <summary>Gets whether one effect attempt is the exact proof-backed reconciled successor of another.</summary>
    public static bool IsDirectSuccessor(GovernedLoopEffectAttempt? currentAttempt, GovernedLoopEffectAttempt? nextAttempt, GovernedLoopEffectReconciliationCase? reconciliationCase)
    {
        if (currentAttempt is null
            || nextAttempt is null
            || reconciliationCase is null
            || !CanCreateSuccessor(currentAttempt, reconciliationCase)
            || GovernedLoopEffectAttemptContract.Validate(nextAttempt) is not null)
        {
            return false;
        }

        var resolution = reconciliationCase.Resolution!;
        var expectedAfterEvidenceId = currentAttempt.AfterEvidenceId ?? resolution.OutcomeEvidenceId;
        return SameAttemptIdentity(currentAttempt, nextAttempt)
            && FixedHashEquals(currentAttempt.ContentHash, nextAttempt.PreviousContentHash)
            && FixedHashEquals(nextAttempt.ContentHash, GovernedLoopEffectAttemptContract.Compute(nextAttempt))
            && string.Equals(currentAttempt.DispatchAuthorityEvidenceHash, nextAttempt.DispatchAuthorityEvidenceHash, StringComparison.Ordinal)
            && string.Equals(currentAttempt.BeforeEvidenceId, nextAttempt.BeforeEvidenceId, StringComparison.Ordinal)
            && string.Equals(expectedAfterEvidenceId, nextAttempt.AfterEvidenceId, StringComparison.Ordinal)
            && nextAttempt.Payload.Phase == GovernedLoopEffectPhase.Reconciled
            && nextAttempt.Payload.Outcome == resolution.Outcome
            && nextAttempt.Payload.EvidenceStatus == GovernedLoopEffectEvidenceStatus.Complete
            && string.Equals(nextAttempt.Payload.OutcomeEvidenceId, resolution.OutcomeEvidenceId, StringComparison.Ordinal)
            && string.Equals(nextAttempt.Payload.ReconciliationEvidenceId, resolution.ResolutionId, StringComparison.Ordinal)
            && nextAttempt.Payload.UpdatedAtUtc == resolution.ResolvedAtUtc;
    }

    private static bool SameAttemptIdentity(GovernedLoopEffectAttempt current, GovernedLoopEffectAttempt next)
        => Equals(current.Binding, next.Binding)
            && string.Equals(current.NodeId, next.NodeId, StringComparison.Ordinal)
            && current.NodeAttempt == next.NodeAttempt
            && Equals(current.Capability, next.Capability)
            && Equals(current.Implementation, next.Implementation)
            && string.Equals(current.ActuatorOperationId, next.ActuatorOperationId, StringComparison.Ordinal)
            && string.Equals(current.OperationDescriptorHash, next.OperationDescriptorHash, StringComparison.Ordinal)
            && string.Equals(current.InputFingerprint, next.InputFingerprint, StringComparison.Ordinal)
            && string.Equals(current.TargetFingerprint, next.TargetFingerprint, StringComparison.Ordinal)
            && string.Equals(current.PreconditionEvidenceHash, next.PreconditionEvidenceHash, StringComparison.Ordinal)
            && string.Equals(current.AdmissionAuthorityEvidenceHash, next.AdmissionAuthorityEvidenceHash, StringComparison.Ordinal)
            && current.Payload.SchemaVersion == next.Payload.SchemaVersion
            && string.Equals(current.Payload.EffectId, next.Payload.EffectId, StringComparison.Ordinal)
            && string.Equals(current.Payload.OperationId, next.Payload.OperationId, StringComparison.Ordinal)
            && current.Payload.EffectGeneration == next.Payload.EffectGeneration
            && current.Payload.Origin == next.Payload.Origin
            && string.Equals(current.Payload.OriginNodeId, next.Payload.OriginNodeId, StringComparison.Ordinal)
            && FixedHashEquals(current.Payload.IntentHash, next.Payload.IntentHash);

    private static bool FixedHashEquals(string? left, string? right)
        => GovernedLoopEffectReconciliationContractValidator.IsCanonicalSha256(left)
            && GovernedLoopEffectReconciliationContractValidator.IsCanonicalSha256(right)
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left!), Encoding.ASCII.GetBytes(right!));
}
