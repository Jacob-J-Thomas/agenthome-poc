namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines why a durable queue entry is terminal.</summary>
public enum TriggerQueueTerminalReason
{
    /// <summary>The entry is nonterminal.</summary>
    None,

    /// <summary>Delivery admission rejected the envelope.</summary>
    AdmissionRejected,

    /// <summary>The active queue count bound was full.</summary>
    QueueCountExceeded,

    /// <summary>The active queue byte bound was full.</summary>
    QueueBytesExceeded,

    /// <summary>The loop's active-entry quota was full.</summary>
    LoopQuotaExceeded,

    /// <summary>An explicit request cancelled the entry.</summary>
    Cancelled,

    /// <summary>The exclusive expiry instant was reached.</summary>
    Expired,

    /// <summary>The inclusive deadline was exceeded.</summary>
    DeadlineExceeded,

    /// <summary>The governed runner accepted the dispatch request.</summary>
    Dispatched,

    /// <summary>Current authority or the governed runner rejected the request before provider dispatch.</summary>
    DispatchRejected,

    /// <summary>The exact provider outcome could not be proved.</summary>
    AmbiguousDispatch
}
