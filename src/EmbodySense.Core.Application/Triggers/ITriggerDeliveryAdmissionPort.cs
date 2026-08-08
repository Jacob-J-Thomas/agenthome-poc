using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>
/// Defines the fail-closed application boundary for classifying trigger-delivery admission evidence.
/// </summary>
/// <remarks>Implementations may classify evidence only; this port does not persist, queue, dispatch, schedule, resume, or execute a loop.</remarks>
public interface ITriggerDeliveryAdmissionPort
{
    /// <summary>
    /// Evaluates one bounded request without granting execution authority.
    /// </summary>
    /// <param name="request">The bounded envelope and exact current evidence.</param>
    /// <param name="cancellationToken">The token used to cancel evaluation.</param>
    /// <returns>The structured admission, replay, conflict, temporal, authority, availability, or validation outcome.</returns>
    Task<TriggerDeliveryAdmissionResult> AdmitAsync(TriggerDeliveryAdmissionRequest request, CancellationToken cancellationToken = default);
}
