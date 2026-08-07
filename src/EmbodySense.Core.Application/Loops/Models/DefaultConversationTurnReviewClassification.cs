namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Classifies the durable evidence that caused a default-conversation turn to require human review.
/// </summary>
public enum DefaultConversationTurnReviewClassification
{
    /// <summary>No supported review classification was identified.</summary>
    Unknown = 0,
    /// <summary>The provider attempt crossed its dispatch boundary, but no terminal outcome was retained.</summary>
    OutcomeUnknown,
    /// <summary>Canonical transcript publication evidence conflicts with the durable turn.</summary>
    TranscriptConflict,
    /// <summary>A provider response was retained, but its required completion audit did not complete.</summary>
    ObservedWithAuditFailure
}
