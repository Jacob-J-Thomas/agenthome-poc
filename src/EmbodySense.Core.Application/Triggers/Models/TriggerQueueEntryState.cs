namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines the durable lifecycle of a trigger queue entry without implying execution.</summary>
public enum TriggerQueueEntryState
{
    /// <summary>The accepted entry is waiting for a later worker child to select it.</summary>
    Queued,

    /// <summary>Delivery admission rejected the envelope.</summary>
    Rejected,

    /// <summary>A queue bound rejected the otherwise admitted envelope.</summary>
    Backpressured,

    /// <summary>An explicit cancellation terminalized the queued entry before selection.</summary>
    Cancelled,

    /// <summary>The queued entry reached its expiry or deadline before selection.</summary>
    Expired
}
