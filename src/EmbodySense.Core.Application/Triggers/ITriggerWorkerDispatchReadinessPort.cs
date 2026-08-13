using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Checks whether selected trigger evidence is ready for irreversible dispatch intent.</summary>
public interface ITriggerWorkerDispatchReadinessPort
{
    /// <summary>Returns the closed pre-intent readiness posture for one exact selected envelope.</summary>
    Task<TriggerWorkerDispatchReadinessResult> CheckAsync(
        TriggerDeliveryEnvelope envelope,
        CancellationToken cancellationToken = default);
}
