using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns one bounded evaluator outcome and the latest known durable state.</summary>
public sealed record ScheduleEvaluationResult(
    ScheduleEvaluationStatus Status,
    string ReasonCode,
    ScheduleState? State);
