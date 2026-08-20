namespace EmbodySense.Core.Persistence.Loops.Models;

internal sealed record ScheduleRunAdmissionRetirement(
    int SchemaVersion,
    string ScheduleId,
    long ScheduleRevision,
    string DefinitionHash,
    long RetiredThroughOccurrenceOrdinal,
    DateTimeOffset RetiredThroughScheduledAtUtc,
    DateTimeOffset RetiredAtUtc);
