namespace EmbodySense.Core.Common.Triggers.Schedules.Models;

/// <summary>Marks one exact occurrence as durably deferred by overlap rather than newly misfired.</summary>
/// <param name="SchemaVersion">The exact schema version, which must be 1.</param>
/// <param name="Occurrence">The exact occurrence retained as the state's next occurrence.</param>
/// <param name="Identity">The deterministic identity reused when overlap clears.</param>
/// <param name="DeferredAtUtc">When the explicit deferral became durable.</param>
public sealed record ScheduleDeferredOccurrence(
    int SchemaVersion,
    ScheduleOccurrence Occurrence,
    ScheduleOccurrenceIdentity Identity,
    DateTimeOffset DeferredAtUtc)
{
    /// <summary>Gets the only supported deferred-occurrence schema version.</summary>
    public const int CurrentSchemaVersion = ScheduleContractLimits.CurrentSchemaVersion;
}
