using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns one exact immutable definition and state snapshot when found.</summary>
public sealed record ScheduleStoreReadResult(
    ScheduleStoreReadStatus Status,
    ScheduleDefinition? Definition,
    ScheduleState? State);
