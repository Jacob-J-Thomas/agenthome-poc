using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns a closed mutation outcome and the authoritative current state when available.</summary>
public sealed record ScheduleStoreMutationResult(
    ScheduleStoreMutationStatus Status,
    ScheduleState? CurrentState);
