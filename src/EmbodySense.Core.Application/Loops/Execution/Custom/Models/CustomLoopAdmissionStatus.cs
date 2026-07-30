using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Identifies the supported custom loop admission status values.
/// </summary>
public enum CustomLoopAdmissionStatus
{
    /// <summary>
    /// Identifies the unknown custom loop admission status.
    /// </summary>
    Unknown = 0,
    /// <summary>
    /// Identifies the admitted custom loop admission status.
    /// </summary>
    Admitted = 1,
    /// <summary>
    /// Identifies the replayed custom loop admission status.
    /// </summary>
    Replayed = 2,
    /// <summary>
    /// Identifies the invalid custom loop admission status.
    /// </summary>
    Invalid = 3,
    /// <summary>
    /// Identifies the conflict custom loop admission status.
    /// </summary>
    Conflict = 4,
    /// <summary>
    /// Identifies the nonterminal run exists custom loop admission status.
    /// </summary>
    NonterminalRunExists = 5,
    /// <summary>
    /// Identifies the limit exceeded custom loop admission status.
    /// </summary>
    LimitExceeded = 6,
    /// <summary>
    /// Identifies the not found custom loop admission status.
    /// </summary>
    NotFound = 7,
    /// <summary>
    /// Identifies the audit unavailable custom loop admission status.
    /// </summary>
    AuditUnavailable = 8,
    /// <summary>
    /// Identifies the receipt unavailable custom loop admission status.
    /// </summary>
    ReceiptUnavailable = 9
}
