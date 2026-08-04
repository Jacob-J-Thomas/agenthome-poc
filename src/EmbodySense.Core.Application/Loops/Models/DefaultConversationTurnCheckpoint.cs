namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>
/// Identifies the last durably proved boundary of one default-conversation turn.
/// </summary>
public enum DefaultConversationTurnCheckpoint
{
    /// <summary>No boundary has been proved.</summary>
    Unknown = 0,
    /// <summary>The turn identity and canonical base transcript were admitted.</summary>
    Admitted,
    /// <summary>The Started loop-run projection was persisted.</summary>
    RunStarted,
    /// <summary>The exact user message and its stable identity were accepted.</summary>
    UserMessageAccepted,
    /// <summary>The user-message transcript publication intent was persisted.</summary>
    UserPublicationPrepared,
    /// <summary>The user message is present exactly once in the canonical transcript.</summary>
    UserPublished,
    /// <summary>The stable provider attempt was prepared before the provider <c>turn/start</c> transport-write boundary.</summary>
    ProviderDispatchPrepared,
    /// <summary>The provider turn/start transport-write boundary was reached, so the external outcome is unknown until observed.</summary>
    ProviderDispatchStarted,
    /// <summary>A terminal provider outcome was durably observed, including exact assistant output for success.</summary>
    ProviderOutcomeObserved,
    /// <summary>The assistant-message transcript publication intent was persisted.</summary>
    AssistantPublicationPrepared,
    /// <summary>The assistant message is present exactly once in the canonical transcript.</summary>
    AssistantPublished,
    /// <summary>The durable and runtime transcript projections were synchronized.</summary>
    TranscriptSynchronized,
    /// <summary>The desired terminal run status and checkpoint were persisted in the protocol.</summary>
    TerminalPrepared,
    /// <summary>The terminal loop-run projection was synchronized with the protocol.</summary>
    Terminal,
    /// <summary>A human explicitly resolved a needs-review turn without replaying its provider attempt.</summary>
    ReviewResolved
}
