using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Reports exact current governed-run overlap without deciding schedule policy.</summary>
public interface IScheduleOverlapPort
{
    /// <summary>Gets overlap evidence for the exact target and deterministic occurrence identity.</summary>
    Task<ScheduleOverlapResult> GetStatusAsync(
        TriggerLoopReference target,
        ScheduleOccurrenceIdentity occurrenceIdentity,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default);
}
