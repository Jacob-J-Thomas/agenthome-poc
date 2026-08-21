using EmbodySense.Core.Common.Triggers.Schedules;
using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Carries one immutable schedule definition and current state across the posture port.</summary>
/// <param name="Definition">The exact immutable schedule definition.</param>
/// <param name="State">The exact current optimistic schedule state.</param>
public sealed record GovernedLoopScheduleEvidenceSnapshot(ScheduleDefinition Definition, ScheduleState State);
