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
    Expired,

    /// <summary>A generation-scoped worker owns the entry but has not recorded dispatch intent.</summary>
    WorkerOwned,

    /// <summary>Durable dispatch intent exists and the provider outcome is not yet durably known.</summary>
    Dispatching,

    /// <summary>The governed runner accepted and terminalized the dispatch request.</summary>
    Dispatched,

    /// <summary>Current authority or the governed runner rejected the request before provider dispatch.</summary>
    DispatchRejected,

    /// <summary>Dispatch may have occurred and requires explicit review before any retry.</summary>
    NeedsReview
}
