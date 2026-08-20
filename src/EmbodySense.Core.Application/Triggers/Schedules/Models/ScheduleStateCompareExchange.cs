using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Requests one exact optimistic state replacement without storage-specific stages.</summary>
public sealed record ScheduleStateCompareExchange(
    ScheduleState Expected,
    ScheduleState Replacement);
