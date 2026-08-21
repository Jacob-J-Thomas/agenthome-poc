namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Classifies one bounded all-pending cancellation operation.</summary>
public enum TriggerQueuePendingCancellationStatus
{
    /// <summary>Every selected nonterminal delivery reached a durable terminal boundary.</summary>
    Applied = 1,

    /// <summary>No nonterminal delivery currently matches the exact loop identity.</summary>
    NoMatches = 2,

    /// <summary>The matching set exceeded the explicit mutation bound and nothing was changed.</summary>
    BoundExceeded = 3,

    /// <summary>A concurrent change prevented at least one selected cancellation.</summary>
    Conflict = 4,

    /// <summary>Some cancellations committed before a later conclusive failure.</summary>
    PartiallyApplied = 5,

    /// <summary>Bounded persistence capacity refused at least one operation.</summary>
    Backpressured = 6,

    /// <summary>The durable queue could not be read or changed safely.</summary>
    Unavailable = 7,

    /// <summary>The request was outside the bounded contract.</summary>
    Invalid = 8
}
