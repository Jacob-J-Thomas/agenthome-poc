using EmbodySense.Core.Application.Triggers.Schedules.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers.Schedules;

/// <summary>Resolves exact accepted schedule-delivery evidence from the authoritative schedule store.</summary>
public interface IScheduleDeliveryProvenancePort
{
    /// <summary>Finds accepted evidence or the exact pending-finalization posture bound to the supplied envelope.</summary>
    Task<ScheduleDeliveryProvenanceResult> ResolveAsync(
        TriggerDeliveryEnvelope envelope,
        CancellationToken cancellationToken = default);
}
