namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Names injectable process-loss boundaries around durable default-conversation operations.
/// </summary>
public enum DefaultConversationTurnBoundary
{
    /// <summary>No concrete boundary.</summary>
    Unknown = 0,
    /// <summary>The protocol record was created.</summary>
    TurnAdmitted,
    /// <summary>The Started run projection was saved.</summary>
    RunStartSaved,
    /// <summary>The Started run projection outcome was checkpointed in the protocol.</summary>
    RunStartCheckpointed,
    /// <summary>The user message was durably accepted.</summary>
    UserAccepted,
    /// <summary>The user publication intent was saved.</summary>
    UserPublicationPrepared,
    /// <summary>The user transcript append committed before its outcome checkpoint.</summary>
    UserTranscriptAppended,
    /// <summary>The user publication outcome was saved.</summary>
    UserPublished,
    /// <summary>The provider attempt was durably prepared.</summary>
    ProviderDispatchPrepared,
    /// <summary>The irreversible provider turn/start transport-write boundary was durably marked.</summary>
    ProviderDispatchStarted,
    /// <summary>The provider output was durably observed.</summary>
    ProviderOutcomeObserved,
    /// <summary>The assistant publication intent was saved.</summary>
    AssistantPublicationPrepared,
    /// <summary>The assistant transcript append committed before its outcome checkpoint.</summary>
    AssistantTranscriptAppended,
    /// <summary>The assistant publication outcome was saved.</summary>
    AssistantPublished,
    /// <summary>The runtime projection was synchronized from the canonical transcript.</summary>
    TranscriptSynchronized,
    /// <summary>The desired terminal status was saved in the protocol.</summary>
    TerminalPrepared,
    /// <summary>The terminal run projection was saved.</summary>
    TerminalRunSaved,
    /// <summary>The terminal checkpoint was committed.</summary>
    TerminalCommitted
}
