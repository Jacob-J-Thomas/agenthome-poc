namespace EmbodySense.Core.Common.Loops.Admission.Models;

/// <summary>Records one immutable successful governed-loop admission receipt.</summary>
/// <param name="SchemaVersion">The receipt schema version, which must be 1.</param>
/// <param name="Intent">The exact immutable intent including workspace, operation, request, publication, grant, role, actor, surface, and graph hashes.</param>
/// <param name="Evidence">The complete exact evidence supporting admission.</param>
/// <param name="RecordedAtUtc">The trusted UTC admission time.</param>
/// <param name="ContentHash">The canonical hash over the complete receipt except this field.</param>
/// <remarks>The receipt is immutable historical evidence and does not itself grant execution or effect authority.</remarks>
public sealed record GovernedLoopAdmissionReceipt(
    int SchemaVersion,
    GovernedLoopAdmissionIntent Intent,
    GovernedLoopAdmissionEvidence Evidence,
    DateTimeOffset RecordedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental receipt schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopAdmissionLimits.CurrentSchemaVersion;
}
