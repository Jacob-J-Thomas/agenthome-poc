namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines the closed outcome of one queue-admission request.</summary>
public enum TriggerQueueAdmissionStatus
{
    /// <summary>A new accepted envelope was durably queued.</summary>
    Queued,

    /// <summary>An existing durable outcome was replayed.</summary>
    Replayed,

    /// <summary>Delivery admission rejected the envelope.</summary>
    Rejected,

    /// <summary>A configured queue bound rejected the envelope.</summary>
    Backpressured,

    /// <summary>Immediate-only mode rejected the request without creating an artifact.</summary>
    ImmediateRejected,

    /// <summary>Safe durable state could not be established or inspected.</summary>
    Unavailable
}
