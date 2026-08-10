using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;

namespace EmbodySense.Core.Application.Loops.Compatibility;

/// <summary>
/// Describes typed effect posture observed in legacy evidence without claiming a canonical intent hash, revision, or execution binding.
/// </summary>
public sealed class GovernedLoopCompatibilityEffectObservation
{
    internal GovernedLoopCompatibilityEffectObservation(
        string effectId,
        string operationId,
        long sourceGeneration,
        GovernedLoopEffectOrigin origin,
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? sourceEvidenceId,
        string? sourceReconciliationEvidenceId,
        DateTimeOffset observedAtUtc)
    {
        EffectId = GovernedLoopCompatibilityValueGuard.RequireSourceIdentifier(effectId, nameof(effectId));
        OperationId = GovernedLoopCompatibilityValueGuard.RequireSourceIdentifier(operationId, nameof(operationId));
        SourceGeneration = GovernedLoopCompatibilityValueGuard.RequireGeneration(sourceGeneration, nameof(sourceGeneration));
        Origin = GovernedLoopCompatibilityValueGuard.RequireConcrete(origin, nameof(origin));
        Phase = GovernedLoopCompatibilityValueGuard.RequireConcrete(phase, nameof(phase));
        Outcome = ValidateOutcome(outcome);
        EvidenceStatus = GovernedLoopCompatibilityValueGuard.RequireConcrete(evidenceStatus, nameof(evidenceStatus));
        SourceEvidenceId = GovernedLoopCompatibilityValueGuard.RequireOptionalSourceIdentifier(sourceEvidenceId, nameof(sourceEvidenceId));
        SourceReconciliationEvidenceId = GovernedLoopCompatibilityValueGuard.RequireOptionalSourceIdentifier(sourceReconciliationEvidenceId, nameof(sourceReconciliationEvidenceId));
        var hasOutcomeEvidence = phase is GovernedLoopEffectPhase.OutcomeObserved or GovernedLoopEffectPhase.Committed
            || phase == GovernedLoopEffectPhase.ReconciliationRequired && outcome == GovernedLoopEffectOutcome.Conflicted;
        if (hasOutcomeEvidence && SourceEvidenceId is null
            || !GovernedLoopExecutionStateMatrix.IsEffectStateValid(Phase, Outcome, EvidenceStatus, hasOutcomeEvidence, SourceReconciliationEvidenceId is not null))
        {
            throw new ArgumentException("The compatibility effect phase, outcome, evidence posture, and typed source references do not form a legal canonical classification.", nameof(phase));
        }

        ObservedAtUtc = GovernedLoopCompatibilityValueGuard.RequireUtc(observedAtUtc, nameof(observedAtUtc));
    }

    /// <summary>Gets the exact stable effect identity retained by the source protocol.</summary>
    public string EffectId { get; }

    /// <summary>Gets the exact stable idempotency or operation identity retained by the source protocol.</summary>
    public string OperationId { get; }

    /// <summary>Gets the exact positive attempt or operation generation retained by the source.</summary>
    public long SourceGeneration { get; }

    /// <summary>Gets the canonical origin classification supported by typed source evidence.</summary>
    public GovernedLoopEffectOrigin Origin { get; }

    /// <summary>Gets the canonical phase classification supported by typed source evidence.</summary>
    public GovernedLoopEffectPhase Phase { get; }

    /// <summary>Gets the external outcome supported by typed source evidence without inferring from prose.</summary>
    public GovernedLoopEffectOutcome Outcome { get; }

    /// <summary>Gets the evidence-completeness posture supported by the source protocol.</summary>
    public GovernedLoopEffectEvidenceStatus EvidenceStatus { get; }

    /// <summary>Gets the exact typed source event or transition identity supporting this observation.</summary>
    public string? SourceEvidenceId { get; }

    /// <summary>Gets exact source disposition evidence when the source retained one.</summary>
    public string? SourceReconciliationEvidenceId { get; }

    /// <summary>Gets the source-owned UTC observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    private static GovernedLoopEffectOutcome ValidateOutcome(GovernedLoopEffectOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Choose a supported effect outcome.");
        }

        return outcome;
    }
}
