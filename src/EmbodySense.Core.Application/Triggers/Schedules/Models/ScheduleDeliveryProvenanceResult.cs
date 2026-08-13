using EmbodySense.Core.Common.Triggers.Schedules.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules.Models;

/// <summary>Returns exact accepted evidence or a closed pending, conflict, or store-failure posture.</summary>
public sealed record ScheduleDeliveryProvenanceResult(
    ScheduleDeliveryProvenanceStatus Status,
    ScheduleDeliveryProvenanceEvidence? Evidence);
