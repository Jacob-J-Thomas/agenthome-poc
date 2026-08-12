namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Records one definitive admitted or rejected outcome for an exact prepared admission intent.</summary>
/// <param name="SchemaVersion">The terminal-outcome schema version, which must be 1.</param>
/// <param name="Intent">The exact prepared admission intent.</param>
/// <param name="Disposition">The definitive admitted or rejected disposition.</param>
/// <param name="Receipt">The successful receipt, present only when admitted.</param>
/// <param name="Rejection">The rejection, present only when rejected.</param>
/// <param name="RecordedAtUtc">The trusted UTC terminal recording time.</param>
/// <param name="ContentHash">The canonical hash over the complete terminal outcome except this field.</param>
/// <remarks>Invalid, conflicting, unavailable, and ambiguous operations do not produce this durable terminal contract.</remarks>
public sealed record GovernedLoopAdmissionTerminalOutcome(
    int SchemaVersion,
    GovernedLoopAdmissionIntent Intent,
    GovernedLoopAdmissionDisposition Disposition,
    GovernedLoopAdmissionReceipt? Receipt,
    GovernedLoopAdmissionRejection? Rejection,
    DateTimeOffset RecordedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental terminal-outcome schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;
}
