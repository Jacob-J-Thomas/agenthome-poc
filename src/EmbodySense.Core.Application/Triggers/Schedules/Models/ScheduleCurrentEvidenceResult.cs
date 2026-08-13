namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns one closed current-evidence outcome.</summary>
public sealed record ScheduleCurrentEvidenceResult(
    ScheduleCurrentEvidenceStatus Status,
    ScheduleCurrentEvidence? Evidence);
