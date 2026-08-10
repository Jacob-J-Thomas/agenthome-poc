namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Identifies one closed, value-free governed-loop execution contract rejection.</summary>
public enum GovernedLoopExecutionValidationErrorCode
{
    /// <summary>No supported error category was supplied.</summary>
    Unknown = 0,
    /// <summary>A required contract was absent.</summary>
    ContractRequired,
    /// <summary>The schema version is unsupported.</summary>
    UnsupportedSchemaVersion,
    /// <summary>A bound child does not use the aggregate's exact execution binding.</summary>
    BindingMismatch,
    /// <summary>An evidence collection exceeds its finite schema bound.</summary>
    CollectionTooLarge,
    /// <summary>An evidence collection is not sorted and unique by stable identity.</summary>
    CollectionNotCanonical,
    /// <summary>Two effect identities claim the same idempotency operation and effect generation.</summary>
    EffectOperationGenerationNotUnique,
    /// <summary>A lifecycle and frontier posture combination is illegal.</summary>
    LifecycleFrontierMismatch,
    /// <summary>An ambiguity-terminal lifecycle does not retain the ambiguity that caused it.</summary>
    ReviewEvidenceRequired,
    /// <summary>A conclusive terminal lifecycle still contains unresolved evidence.</summary>
    TerminalEvidenceUnresolved,
    /// <summary>An effect origin names a graph node absent from the retained frontier.</summary>
    EffectOriginNodeMissing,
    /// <summary>A node-attributed effect names a node posture that cannot have dispatched work.</summary>
    EffectOriginNodeNotExecutable,
    /// <summary>A projection source does not resolve to the bound run or retained node/effect evidence.</summary>
    ProjectionSourceMissing,
    /// <summary>A projection effect reference does not resolve to the retained source effect.</summary>
    ProjectionEffectMismatch,
    /// <summary>An evidence timestamp lies outside the lifecycle observation interval.</summary>
    TimestampOutsideLifecycle,
    /// <summary>A proposed state transition is illegal.</summary>
    IllegalTransition,
    /// <summary>A successor optimistic version is not exactly one greater than its predecessor.</summary>
    InvalidSuccessorVersion,
    /// <summary>An immutable identity or attribution changed across a transition.</summary>
    ImmutableEvidenceChanged,
    /// <summary>Previously retained effect or projection evidence is absent from a proposed successor.</summary>
    HistoricalEvidenceMissing
}
