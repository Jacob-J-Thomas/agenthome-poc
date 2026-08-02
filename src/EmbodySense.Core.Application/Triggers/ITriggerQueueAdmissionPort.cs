using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Defines fail-closed trigger queue admission without selection or dispatch.</summary>
public interface ITriggerQueueAdmissionPort
{
    /// <summary>Evaluates and, only when requested and admitted, durably queues one delivery.</summary>
    /// <param name="request">The bounded request.</param>
    /// <param name="cancellationToken">A token honored before the durable commit boundary.</param>
    /// <returns>The closed admission outcome.</returns>
    Task<TriggerQueueAdmissionResult> AdmitAsync(TriggerQueueAdmissionRequest request, CancellationToken cancellationToken = default);
}
