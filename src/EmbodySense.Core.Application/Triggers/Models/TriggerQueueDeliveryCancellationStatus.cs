namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Classifies one safe application-level delivery cancellation.</summary>
public enum TriggerQueueDeliveryCancellationStatus
{
    /// <summary>The exact nonterminal delivery was durably cancelled or moved to review after dispatch intent.</summary>
    Applied = 1,

    /// <summary>The delivery is already terminal and no new mutation occurred.</summary>
    AlreadyTerminal = 2,

    /// <summary>No matching delivery exists.</summary>
    NotFound = 3,

    /// <summary>The expected revision was stale.</summary>
    Conflict = 4,

    /// <summary>Bounded persistence capacity refused the mutation.</summary>
    Backpressured = 5,

    /// <summary>The durable queue could not be read or changed safely.</summary>
    Unavailable = 6,

    /// <summary>The request was outside the bounded contract.</summary>
    Invalid = 7
}
