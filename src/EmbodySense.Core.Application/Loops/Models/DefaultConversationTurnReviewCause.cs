namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>Identifies the durable evidence that caused a default-conversation turn to require review.</summary>
public enum DefaultConversationTurnReviewCause
{
    /// <summary>No review cause is retained.</summary>
    None = 0,
    /// <summary>The provider dispatch crossed its irreversible boundary without a retained terminal outcome.</summary>
    OutcomeUnknown,
    /// <summary>Transcript publication or recovery evidence conflicted with the durable turn.</summary>
    TranscriptConflict,
    /// <summary>A provider response was retained but its required completion audit failed.</summary>
    ObservedWithAuditFailure
}
