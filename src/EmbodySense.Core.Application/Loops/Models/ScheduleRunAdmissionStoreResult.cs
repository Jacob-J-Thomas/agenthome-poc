using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Triggers.Schedules;

namespace EmbodySense.Core.Application.Loops.Models;

/// <summary>Returns one exact schedule-aware run-store disposition and its durable evidence.</summary>
/// <param name="Status">The closed store status.</param>
/// <param name="Run">The exact created, replayed, or blocking run when available.</param>
/// <param name="Evidence">The exact durable occurrence disposition evidence when committed.</param>
public sealed record ScheduleRunAdmissionStoreResult(
    ScheduleRunAdmissionStoreStatus Status,
    CustomLoopRunRecord? Run,
    ScheduleRunAdmissionEvidence? Evidence);
