using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Provides operations for custom loop admission status names.
/// </summary>
public static class CustomLoopAdmissionStatusNames
{
    /// <summary>
    /// Identifies the unknown custom loop admission status names.
    /// </summary>
    public const string Unknown = nameof(CustomLoopAdmissionStatus.Unknown);
    /// <summary>
    /// Identifies the admitted custom loop admission status names.
    /// </summary>
    public const string Admitted = nameof(CustomLoopAdmissionStatus.Admitted);
    /// <summary>
    /// Identifies the replayed custom loop admission status names.
    /// </summary>
    public const string Replayed = nameof(CustomLoopAdmissionStatus.Replayed);
    /// <summary>
    /// Identifies the invalid custom loop admission status names.
    /// </summary>
    public const string Invalid = nameof(CustomLoopAdmissionStatus.Invalid);
    /// <summary>
    /// Identifies the conflict custom loop admission status names.
    /// </summary>
    public const string Conflict = nameof(CustomLoopAdmissionStatus.Conflict);
    /// <summary>
    /// Identifies the nonterminal run exists custom loop admission status names.
    /// </summary>
    public const string NonterminalRunExists = nameof(CustomLoopAdmissionStatus.NonterminalRunExists);
    /// <summary>
    /// Identifies the limit exceeded custom loop admission status names.
    /// </summary>
    public const string LimitExceeded = nameof(CustomLoopAdmissionStatus.LimitExceeded);
    /// <summary>
    /// Identifies the not found custom loop admission status names.
    /// </summary>
    public const string NotFound = nameof(CustomLoopAdmissionStatus.NotFound);
    /// <summary>
    /// Identifies the audit unavailable custom loop admission status names.
    /// </summary>
    public const string AuditUnavailable = nameof(CustomLoopAdmissionStatus.AuditUnavailable);
    /// <summary>
    /// Identifies the receipt unavailable custom loop admission status names.
    /// </summary>
    public const string ReceiptUnavailable = nameof(CustomLoopAdmissionStatus.ReceiptUnavailable);
}
