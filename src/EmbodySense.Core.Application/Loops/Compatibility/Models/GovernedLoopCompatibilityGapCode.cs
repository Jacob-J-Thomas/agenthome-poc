namespace EmbodySense.Core.Application.Loops.Compatibility.Models;

/// <summary>Identifies one static, non-sensitive reason that legacy evidence cannot become canonical execution truth.</summary>
public enum GovernedLoopCompatibilityGapCode
{
    /// <summary>No compatibility gap exists.</summary>
    Unknown = 0,
    /// <summary>The source failed its authoritative public validator.</summary>
    SourceValidationFailed = 1,
    /// <summary>The source does not bind an exact canonical graph revision.</summary>
    ExactRevisionUnavailable = 2,
    /// <summary>The source does not bind every plane to one canonical execution identity and generation.</summary>
    ExecutionBindingUnavailable = 3,
    /// <summary>The source checkpoint is not a canonical durable graph frontier.</summary>
    DurableFrontierUnavailable = 4,
    /// <summary>The source does not retain the provider dispatch boundary required for canonical effect posture.</summary>
    ProviderDispatchBoundaryUnavailable = 5,
    /// <summary>The source does not retain canonical effect-audit completion evidence.</summary>
    EffectAuditCompletionUnavailable = 6,
    /// <summary>A prepared publication does not prove whether its dispatch boundary was crossed.</summary>
    PublicationDispatchBoundaryUnavailable = 7,
    /// <summary>A legacy publication outcome combines meanings that the canonical contract keeps distinct.</summary>
    PublicationOutcomeConflated = 8,
    /// <summary>The source does not retain a canonical versioned projection synchronization fact.</summary>
    ProjectionEvidenceUnavailable = 9,
    /// <summary>The source terminal failure is not classified by the canonical failure taxonomy.</summary>
    CanonicalFailureUnavailable = 10,
    /// <summary>The source review record is not a canonical reconciliation or operator-disposition fact.</summary>
    ReviewDispositionUnavailable = 11,
    /// <summary>The source lifecycle history cannot substitute for canonical lifecycle transition evidence.</summary>
    CanonicalLifecycleHistoryUnavailable = 12,
    /// <summary>The source does not retain a canonical effect intent or its exact hash.</summary>
    CanonicalEffectIntentUnavailable = 13,
    /// <summary>The source does not retain the governed actuator's irreversible dispatch boundary.</summary>
    ActuatorDispatchBoundaryUnavailable = 14,
    /// <summary>The source exceeds a read-only adapter safety bound or lacks the bounded shape required before validation.</summary>
    AdapterInputBoundsExceeded = 15
}
