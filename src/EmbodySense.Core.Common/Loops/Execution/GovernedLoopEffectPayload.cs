namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Contains reusable unbound, value-free evidence for one externally meaningful effect attempt.</summary>
/// <remarks>Construction validates all orthogonal effect-state axes before returning the immutable payload.</remarks>
public sealed record GovernedLoopEffectPayload
{
    private GovernedLoopEffectPayload(
        int schemaVersion,
        string effectId,
        string operationId,
        long effectGeneration,
        GovernedLoopEffectOrigin origin,
        string? originNodeId,
        string intentHash,
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? outcomeEvidenceId,
        string? reconciliationEvidenceId,
        DateTimeOffset updatedAtUtc)
    {
        SchemaVersion = schemaVersion;
        EffectId = effectId;
        OperationId = operationId;
        EffectGeneration = effectGeneration;
        Origin = origin;
        OriginNodeId = originNodeId;
        IntentHash = intentHash;
        Phase = phase;
        Outcome = outcome;
        EvidenceStatus = evidenceStatus;
        OutcomeEvidenceId = outcomeEvidenceId;
        ReconciliationEvidenceId = reconciliationEvidenceId;
        UpdatedAtUtc = updatedAtUtc;
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable effect identity.</summary>
    public string EffectId { get; }

    /// <summary>Gets the stable idempotency operation identity.</summary>
    public string OperationId { get; }

    /// <summary>Gets the positive effect generation.</summary>
    public long EffectGeneration { get; }

    /// <summary>Gets the executor-neutral effect origin.</summary>
    public GovernedLoopEffectOrigin Origin { get; }

    /// <summary>Gets the exact originating graph-node identity when the effect came from a node.</summary>
    public string? OriginNodeId { get; }

    /// <summary>Gets the canonical lowercase SHA-256 hash of the retained value-free intent.</summary>
    public string IntentHash { get; }

    /// <summary>Gets the durable effect phase.</summary>
    public GovernedLoopEffectPhase Phase { get; }

    /// <summary>Gets the observed external outcome independently from phase.</summary>
    public GovernedLoopEffectOutcome Outcome { get; }

    /// <summary>Gets the durable evidence-completion posture.</summary>
    public GovernedLoopEffectEvidenceStatus EvidenceStatus { get; }

    /// <summary>Gets the retained value-free outcome evidence identity when an outcome was observed.</summary>
    public string? OutcomeEvidenceId { get; }

    /// <summary>Gets the retained reconciliation or authenticated human-disposition evidence identity.</summary>
    public string? ReconciliationEvidenceId { get; }

    /// <summary>Gets the UTC timestamp of this committed effect version.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Creates validated reusable unbound effect evidence.</summary>
    /// <param name="schemaVersion">The schema version, which must be 1.</param>
    /// <param name="effectId">The stable effect identity.</param>
    /// <param name="operationId">The stable idempotency operation identity.</param>
    /// <param name="effectGeneration">The positive bounded effect generation.</param>
    /// <param name="origin">The executor-neutral effect origin.</param>
    /// <param name="originNodeId">The exact originating node identity, when applicable.</param>
    /// <param name="intentHash">The canonical lowercase SHA-256 intent hash.</param>
    /// <param name="phase">The durable effect phase.</param>
    /// <param name="outcome">The observed external outcome.</param>
    /// <param name="evidenceStatus">The durable evidence-completion posture.</param>
    /// <param name="outcomeEvidenceId">The retained outcome evidence identity, when required.</param>
    /// <param name="reconciliationEvidenceId">The retained reconciliation or human-disposition evidence identity, when required.</param>
    /// <param name="updatedAtUtc">The UTC timestamp of this committed effect version.</param>
    /// <returns>The validated effect payload.</returns>
    public static GovernedLoopEffectPayload Create(
        int schemaVersion,
        string effectId,
        string operationId,
        long effectGeneration,
        GovernedLoopEffectOrigin origin,
        string? originNodeId,
        string intentHash,
        GovernedLoopEffectPhase phase,
        GovernedLoopEffectOutcome outcome,
        GovernedLoopEffectEvidenceStatus evidenceStatus,
        string? outcomeEvidenceId,
        string? reconciliationEvidenceId,
        DateTimeOffset updatedAtUtc)
    {
        GovernedLoopExecutionContractGuard.RequireSchema(schemaVersion, nameof(schemaVersion));
        if (!GovernedLoopExecutionStateMatrix.IsSupported(origin))
        {
            throw new ArgumentException("A supported governed-loop effect origin is required.", nameof(origin));
        }

        var nodeReference = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(originNodeId, nameof(originNodeId));
        if (!GovernedLoopExecutionStateMatrix.IsEffectOriginNodeValid(origin, nodeReference is not null))
        {
            throw new ArgumentException("Provider, actuator, and memory-mutation effects require their exact originating node identity.", nameof(originNodeId));
        }

        var outcomeReference = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(outcomeEvidenceId, nameof(outcomeEvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        var reconciliationReference = GovernedLoopExecutionContractGuard.RequireOptionalIdentifier(reconciliationEvidenceId, nameof(reconciliationEvidenceId), GovernedLoopExecutionLimits.MaxEvidenceReferenceCharacters);
        if (!GovernedLoopExecutionStateMatrix.IsEffectStateValid(phase, outcome, evidenceStatus, outcomeReference is not null, reconciliationReference is not null))
        {
            throw new ArgumentException("Effect phase, outcome, evidence, and reconciliation references form an illegal state.", nameof(phase));
        }

        return new GovernedLoopEffectPayload(
            schemaVersion,
            GovernedLoopExecutionContractGuard.RequireIdentifier(effectId, nameof(effectId)),
            GovernedLoopExecutionContractGuard.RequireIdentifier(operationId, nameof(operationId)),
            GovernedLoopExecutionContractGuard.RequirePositiveVersion(effectGeneration, nameof(effectGeneration)),
            origin,
            nodeReference,
            GovernedLoopExecutionContractGuard.RequireSha256(intentHash, nameof(intentHash)),
            phase,
            outcome,
            evidenceStatus,
            outcomeReference,
            reconciliationReference,
            GovernedLoopExecutionContractGuard.RequireUtc(updatedAtUtc, nameof(updatedAtUtc)));
    }
}
