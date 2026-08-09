namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Defines whether an admitted delivery may wait in the durable queue.</summary>
public enum TriggerQueueAdmissionMode
{
    /// <summary>Allows durable queue admission subject to configured bounds.</summary>
    Queued,

    /// <summary>Requires immediate handling and therefore never creates a queue artifact in this non-dispatching component.</summary>
    ImmediateOnly
}
