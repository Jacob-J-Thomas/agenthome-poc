namespace EmbodySense.Core.Common.Loops.Models.Custom.Retention;

/// <summary>
/// Identifies whether an idempotency identity retains an exact receipt, only compact expiry proof, or no evidence.
/// </summary>
public enum CustomLoopReceiptOperationLookupStatus
{
    /// <summary>
    /// No lookup status was supplied.
    /// </summary>
    UnknownStatus = 0,

    /// <summary>
    /// A full receipt remains available for exact replay.
    /// </summary>
    Exact,

    /// <summary>
    /// Compact proof establishes that the operation existed but its exact replay receipt expired.
    /// </summary>
    Expired,

    /// <summary>
    /// Neither a full receipt nor compact proof recognizes the operation identity.
    /// </summary>
    Unknown
}
