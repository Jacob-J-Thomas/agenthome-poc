using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>
/// Binds one prepared time-trigger delivery to the immutable schedule coordinates and
/// pre-queue overlap evidence that selected it.
/// </summary>
/// <remarks>
/// The directive is execution evidence only. It does not grant authority or replace
/// current admission, lease, lifecycle, or overlap checks.
/// </remarks>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="ScheduleId">The stable schedule identifier.</param>
/// <param name="DefinitionRevision">The exact immutable definition revision.</param>
/// <param name="DefinitionHash">The lowercase SHA-256 definition digest.</param>
/// <param name="Occurrence">The exact selected occurrence.</param>
/// <param name="Identity">The deterministic occurrence, delivery, and deduplication identities.</param>
/// <param name="Target">The exact governed publication target.</param>
/// <param name="Overlap">The overlap policy evaluated before queue admission.</param>
/// <param name="PreQueueOverlapEvidenceHash">The lowercase SHA-256 digest of the exact pre-queue overlap evidence.</param>
public sealed record ScheduleExecutionDirective(
    int SchemaVersion,
    ScheduleId ScheduleId,
    long DefinitionRevision,
    string DefinitionHash,
    ScheduleOccurrence Occurrence,
    ScheduleOccurrenceIdentity Identity,
    TriggerLoopReference Target,
    ScheduleOverlapPolicy Overlap,
    string PreQueueOverlapEvidenceHash)
{
    /// <summary>Gets the only supported directive schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
