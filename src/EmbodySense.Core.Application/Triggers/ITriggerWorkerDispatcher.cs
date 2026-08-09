using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Invokes one governed runner only after durable dispatch intent.</summary>
public interface ITriggerWorkerDispatcher
{
    /// <summary>Dispatches an intent-bound envelope; cancellation or transport loss must return or be treated as ambiguous.</summary>
    /// <param name="envelope">The exact selected canonical envelope.</param>
    /// <param name="intent">The durable request and authority binding recorded before this call.</param>
    /// <param name="cancellationToken">A token for the provider call; callers must not use cancellation as proof of non-dispatch.</param>
    /// <returns>The accepted, proved-rejected, terminal, or ambiguous provider posture.</returns>
    Task<TriggerWorkerDispatchResult> DispatchAsync(TriggerDeliveryEnvelope envelope, TriggerDispatchEvidence intent, CancellationToken cancellationToken = default);
}
