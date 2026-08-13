using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Startup.Triggers.Schedules.Models;

/// <summary>Returns one closed creation outcome and authoritative current state when available.</summary>
public sealed record ScheduleRuntimeCreateResult(
    ScheduleRuntimeCreateStatus Status,
    ScheduleState? CurrentState);
