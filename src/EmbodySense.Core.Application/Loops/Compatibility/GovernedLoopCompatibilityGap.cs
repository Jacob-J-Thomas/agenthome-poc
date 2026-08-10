namespace EmbodySense.Core.Application.Loops.Compatibility.Models;

/// <summary>Provides one bounded, server-authored compatibility gap without projecting source prose.</summary>
public sealed record GovernedLoopCompatibilityGap
{
    /// <summary>The maximum UTF-16 length of a public compatibility-gap detail.</summary>
    public const int MaxDetailCharacters = 512;

    private GovernedLoopCompatibilityGap(GovernedLoopCompatibilityGapCode code, string detail)
    {
        Code = code;
        Detail = detail;
    }

    /// <summary>Gets the stable gap classification.</summary>
    public GovernedLoopCompatibilityGapCode Code { get; }

    /// <summary>Gets the bounded static explanation authored by this compatibility adapter.</summary>
    public string Detail { get; }

    internal static GovernedLoopCompatibilityGap Create(GovernedLoopCompatibilityGapCode code)
    {
        var detail = code switch
        {
            GovernedLoopCompatibilityGapCode.SourceValidationFailed => "The source failed its authoritative public validator and no source fields were projected.",
            GovernedLoopCompatibilityGapCode.ExactRevisionUnavailable => "The source predates immutable canonical graph revisions and cannot identify an exact executable revision.",
            GovernedLoopCompatibilityGapCode.ExecutionBindingUnavailable => "The source cannot bind lifecycle, frontier, effect, and projection evidence to one canonical execution generation.",
            GovernedLoopCompatibilityGapCode.DurableFrontierUnavailable => "The source checkpoint is runtime-specific and cannot be treated as a canonical durable graph frontier.",
            GovernedLoopCompatibilityGapCode.ProviderDispatchBoundaryUnavailable => "The source does not retain enough typed evidence to prove the canonical provider dispatch boundary.",
            GovernedLoopCompatibilityGapCode.EffectAuditCompletionUnavailable => "The source does not retain a canonical effect-evidence completion or audit status.",
            GovernedLoopCompatibilityGapCode.PublicationDispatchBoundaryUnavailable => "The prepared publication intent does not prove whether the external publication boundary was crossed.",
            GovernedLoopCompatibilityGapCode.PublicationOutcomeConflated => "The source combines omitted, definitely failed, and uncertain publication outcomes in one typed value.",
            GovernedLoopCompatibilityGapCode.ProjectionEvidenceUnavailable => "The source does not retain the optimistic versions required for canonical projection synchronization evidence.",
            GovernedLoopCompatibilityGapCode.CanonicalFailureUnavailable => "The source terminal failure cannot be assigned a canonical failure classification from typed evidence alone.",
            GovernedLoopCompatibilityGapCode.ReviewDispositionUnavailable => "The source review state is not canonical reconciliation or authenticated operator-disposition evidence.",
            GovernedLoopCompatibilityGapCode.CanonicalLifecycleHistoryUnavailable => "The source transition history remains runtime-specific and cannot become canonical lifecycle transition evidence.",
            GovernedLoopCompatibilityGapCode.CanonicalEffectIntentUnavailable => "The source does not retain a canonical effect intent or intent hash, so the mapped posture remains a compatibility observation.",
            GovernedLoopCompatibilityGapCode.ActuatorDispatchBoundaryUnavailable => "The source does not retain the governed actuator's irreversible dispatch boundary independently from governance and outcome evidence.",
            GovernedLoopCompatibilityGapCode.AdapterInputBoundsExceeded => "The source exceeds a read-only adapter safety bound or lacks the bounded shape required before authoritative validation.",
            _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Choose a supported compatibility gap.")
        };

        if (detail.Length > MaxDetailCharacters)
        {
            throw new InvalidOperationException("A server-authored compatibility gap exceeded its public bound.");
        }

        return new GovernedLoopCompatibilityGap(code, detail);
    }
}
