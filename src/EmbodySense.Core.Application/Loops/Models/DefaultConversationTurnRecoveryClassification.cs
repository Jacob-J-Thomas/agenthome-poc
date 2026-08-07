namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Classifies the restart window proved by durable turn evidence.
/// </summary>
public enum DefaultConversationTurnRecoveryClassification
{
    /// <summary>No classification.</summary>
    Unknown = 0,
    /// <summary>The provider <c>turn/start</c> transport-write boundary was definitely not reached.</summary>
    PreDispatch,
    /// <summary>The provider turn/start transport-write boundary was reached but no outcome was observed.</summary>
    ProviderOutcomeUnknown,
    /// <summary>The provider outcome was observed and its publication was safely repaired.</summary>
    ProviderOutcomeObserved,
    /// <summary>A transcript append committed without its later checkpoint and was reconciled.</summary>
    TranscriptPartial,
    /// <summary>The transcript was complete and only terminal run synchronization was missing.</summary>
    TerminalStatusMissing,
    /// <summary>Concurrent or external transcript state conflicts with the retained intent.</summary>
    Conflict
}
