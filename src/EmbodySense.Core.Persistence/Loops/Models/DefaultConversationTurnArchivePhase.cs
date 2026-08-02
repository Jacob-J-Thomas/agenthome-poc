namespace EmbodySense.Core.Persistence.Loops.Models;

/// <summary>Identifies an observable phase of identity-bound terminal archival.</summary>
public enum DefaultConversationTurnArchivePhase
{
    /// <summary>The validated or written active pathname is about to be claimed.</summary>
    BeforeSourceClaim,

    /// <summary>The exact active source has been claimed and retained retirement evidence is about to be revalidated before history staging.</summary>
    AfterSourceClaimBeforeRetirementEvidenceRevalidation,

    /// <summary>Current retirement-evidence handles have been opened and their pathnames are about to be revalidated against those handles.</summary>
    AfterRetirementEvidenceProofOpenBeforePathRevalidation,

    /// <summary>The final retirement-evidence proof is held open and canonical history is about to be published.</summary>
    AfterFinalRetirementEvidenceValidationBeforeHistoryPublication,

    /// <summary>The exact history staging object has been validated and is about to be published without replacement.</summary>
    BeforeHistoryPublication,

    /// <summary>Exact canonical history is durable and the claimed source is about to become its immutable identity proof.</summary>
    AfterHistoryPublication,

    /// <summary>The claimed source has become the immutable identity proof and is about to be revalidated.</summary>
    AfterSourceProofPublication,

    /// <summary>A terminal update has atomically published its staged object and is about to claim that exact object for archival.</summary>
    AfterTerminalWritePublication,

    /// <summary>Canonical history has been published and is about to be revalidated before source-proof publication.</summary>
    BeforeInitialHistoryRevalidation,

    /// <summary>Canonical history and its source proof have been validated independently and are about to be revalidated together.</summary>
    BeforeFinalHistoryRevalidation,

    /// <summary>An owned history stage contains an incomplete prefix and is about to write its remaining bytes.</summary>
    AfterPartialHistoryStageWrite,

    /// <summary>The incomplete history stage has been re-proved through a retained handle and is about to be claimed for retirement.</summary>
    BeforeIncompleteHistoryStageRetirement
}
