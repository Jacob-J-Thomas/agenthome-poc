namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>Identifies an observable phase of identity-bound terminal archival.</summary>
public enum DefaultConversationTurnArchivePhase
{
    /// <summary>The validated or written active pathname is about to be claimed.</summary>
    BeforeSourceClaim,

    /// <summary>The exact history staging object has been validated and is about to be published without replacement.</summary>
    BeforeHistoryPublication,

    /// <summary>Exact canonical history is durable and the claimed source is about to become its immutable identity proof.</summary>
    AfterHistoryPublication,

    /// <summary>The claimed source has become the immutable identity proof and is about to be revalidated.</summary>
    AfterSourceProofPublication,

    /// <summary>A terminal update has atomically published its staged object and is about to claim that exact object for archival.</summary>
    AfterTerminalWritePublication
}
