namespace EmbodySense.Core.Application.HumanReview.Models;

/// <summary>Describes the result of reconciling one retained wake-less decision-action reservation.</summary>
public enum HumanReviewDecisionActionPublicationRecoveryItemStatus
{
    /// <summary>No supported result was supplied.</summary>
    Unknown = 0,
    /// <summary>The deterministic wake was durably published.</summary>
    Published = 1,
    /// <summary>The exact deterministic wake was already durably published.</summary>
    Replayed = 2,
    /// <summary>The reservation changed, is unavailable, or cannot yet be safely reconciled.</summary>
    Parked = 3,
    /// <summary>The retained reservation evidence was invalid or corrupt.</summary>
    Invalid = 4,
}
