namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns exact overlap status and its bounded canonical evidence hash.</summary>
public sealed record ScheduleOverlapResult(
    ScheduleOverlapStatus Status,
    string? EvidenceHash);
