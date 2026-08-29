namespace EmbodySense.Core.Common.Loops.HumanInput.Checkpoints.Models;

/// <summary>Identifies one closed append-only checkpoint evidence boundary.</summary>
public enum GovernedLoopHumanInputWaitingCheckpointEvidenceKind
{
    /// <summary>No supported evidence kind was supplied.</summary>
    Unknown = 0,

    /// <summary>The immutable request checkpoint became durable and pending.</summary>
    Published = 1,

    /// <summary>An exact privacy-safe answer selection was recorded without resuming execution.</summary>
    Answered = 2,

    /// <summary>The trusted request deadline was reached without an accepted answer selection.</summary>
    Expired = 3,

    /// <summary>The governing run cancelled the checkpoint.</summary>
    Cancelled = 4,

    /// <summary>A distinct immutable checkpoint replaced this one.</summary>
    Superseded = 5,

    /// <summary>A later runner recorded terminal consumption without this contract performing a resume.</summary>
    Terminalized = 6,

    /// <summary>An authenticated lifecycle actor rejected the exact request without submitting response data.</summary>
    Rejected = 7,

    /// <summary>The lifecycle superseded the exact request but did not prove the distinct replacement checkpoint required for automatic routing.</summary>
    NeedsReview = 8
}
