namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Identifies the closed Application disposition of one re-read Human Review decision or continuation claim.</summary>
public enum HumanReviewContinuationConsumptionStatus
{
    /// <summary>No supported disposition was produced.</summary>
    Invalid = 0,

    /// <summary>A declared decision path was safely prepared without a continuation release.</summary>
    DecisionPathPrepared = 1,

    /// <summary>The exact approved non-effect continuation was prepared for release.</summary>
    ContinuationReleasePrepared = 2,

    /// <summary>The exact approved effect attempt was proved not started and prepared for release.</summary>
    EffectReleasePrepared = 3,

    /// <summary>The exact approved wake must be retired before any release.</summary>
    RetirementRequired = 4,

    /// <summary>Trusted canonical state or authority was unavailable; state remains parked without a release or retirement request.</summary>
    Unavailable = 5,

    /// <summary>The evaluated claim lease expired while its wake remains live, so a canonical store may fence it and allow a later takeover.</summary>
    StaleClaim = 6,
}
