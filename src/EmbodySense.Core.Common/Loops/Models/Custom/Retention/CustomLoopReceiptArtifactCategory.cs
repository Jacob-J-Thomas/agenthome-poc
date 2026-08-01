namespace EmbodySense.Core.Common.Loops.Models.Custom.Retention;

/// <summary>
/// Classifies retained custom-loop receipt evidence for posture and cleanup safety.
/// </summary>
public enum CustomLoopReceiptArtifactCategory
{
    /// <summary>
    /// No category was supplied.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Complete evidence remains inside the exact replay horizon.
    /// </summary>
    Live,

    /// <summary>
    /// An operation has not reached a terminal outcome.
    /// </summary>
    Pending,

    /// <summary>
    /// A terminal outcome has not been durably audited.
    /// </summary>
    Unaudited,

    /// <summary>
    /// Evidence is readable but carries an integrity or recovery warning.
    /// </summary>
    Degraded,

    /// <summary>
    /// Complete, audited, expired evidence can be compacted under a governed cleanup journal.
    /// </summary>
    Compactable,

    /// <summary>
    /// A complete Create receipt remains the required raw lineage of a live definition after exact replay expires.
    /// </summary>
    RetainedLiveLineage,

    /// <summary>
    /// Compact proof preserves definition lineage or loop-identity non-reuse.
    /// </summary>
    RetainedLineage,

    /// <summary>
    /// Compact proof preserves an expired idempotency identity and request/outcome fingerprints.
    /// </summary>
    ExpiredIdempotency,

    /// <summary>
    /// Evidence is corrupt or fails canonical validation.
    /// </summary>
    Corrupt,

    /// <summary>
    /// Cross-process ownership cannot be established safely.
    /// </summary>
    OwnershipUnresolved,

    /// <summary>
    /// Multiple artifacts or transitions make ownership or lineage ambiguous.
    /// </summary>
    Ambiguous
}
