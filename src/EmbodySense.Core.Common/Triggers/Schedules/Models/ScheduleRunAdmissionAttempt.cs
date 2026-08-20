namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Retains one immutable atomic run-admission observation for a schedule occurrence.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Ordinal">The one-based attempt ordinal within the occurrence evidence.</param>
/// <param name="Disposition">The exact policy-specific run-store disposition.</param>
/// <param name="AdmissionOperationId">The canonical admission operation presented at this boundary.</param>
/// <param name="CandidateRunId">The candidate canonical run identity presented at this boundary.</param>
/// <param name="BlockingRunId">The exact nonterminal run that blocked materialization, or null when a run was created.</param>
/// <param name="RecordedAtUtc">The trusted UTC persistence instant.</param>
public sealed record ScheduleRunAdmissionAttempt(
    int SchemaVersion,
    int Ordinal,
    ScheduleRunAdmissionDisposition Disposition,
    string AdmissionOperationId,
    string CandidateRunId,
    string? BlockingRunId,
    DateTimeOffset RecordedAtUtc)
{
    /// <summary>Gets the only supported attempt schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
