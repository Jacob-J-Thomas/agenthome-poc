namespace EmbodySense.Core.Common.Triggers.Models;

/// <summary>
/// Captures bounded redelivery evidence without treating redelivery as authority or replay proof.
/// </summary>
public sealed record TriggerRedeliveryEvidence
{
    internal TriggerRedeliveryEvidence(int attempt, int count, TriggerDeliveryId originalDeliveryId)
    {
        Attempt = attempt;
        Count = count;
        OriginalDeliveryId = originalDeliveryId;
    }

    /// <summary>Gets the one-based attempt number.</summary>
    public int Attempt { get; }

    /// <summary>Gets the one-based total delivery count observed by the adapter.</summary>
    public int Count { get; }

    /// <summary>Gets the stable original delivery identifier.</summary>
    public TriggerDeliveryId OriginalDeliveryId { get; }
}
