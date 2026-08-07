namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Signals that a persistence mutation cannot reserve its bounded authenticated tombstones before staging.</summary>
internal sealed class TriggerQueuePersistenceBackpressureException : InvalidOperationException
{
    /// <summary>Initializes the bounded persistence backpressure signal.</summary>
    public TriggerQueuePersistenceBackpressureException()
        : base("Trigger queue persistence cannot reserve its worst-case authenticated tombstones for this mutation.")
    {
    }
}
