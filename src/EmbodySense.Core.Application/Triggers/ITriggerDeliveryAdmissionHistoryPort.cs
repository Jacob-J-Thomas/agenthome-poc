using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>
/// Provides bounded server-owned terminal admission history by delivery or deduplication identity.
/// </summary>
/// <remarks>Implementations own provenance. Untrusted request data must never implement or bypass this composition-owned port.</remarks>
public interface ITriggerDeliveryAdmissionHistoryPort
{
    /// <summary>
    /// Finds prior terminal history independently by both supplied identities.
    /// </summary>
    /// <param name="deliveryId">The canonical delivery identity.</param>
    /// <param name="deduplicationId">The canonical deduplication identity.</param>
    /// <param name="cancellationToken">A token that cancels the lookup.</param>
    /// <returns>A bounded available result with independent matches, or an explicit unavailable result when history cannot be inspected safely.</returns>
    Task<TriggerDeliveryAdmissionHistoryLookupResult> FindAsync(TriggerDeliveryId deliveryId, TriggerDeduplicationId deduplicationId, CancellationToken cancellationToken = default);
}
