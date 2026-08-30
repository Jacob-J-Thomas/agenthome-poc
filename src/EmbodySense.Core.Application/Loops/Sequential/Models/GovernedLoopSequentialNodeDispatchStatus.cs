namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies one exact sequential node-dispatch decision.</summary>
public enum GovernedLoopSequentialNodeDispatchStatus
{
    /// <summary>No supported decision was produced.</summary>
    Unknown = 0,
    /// <summary>The exact handler completed with retained evidence.</summary>
    Completed,
    /// <summary>The exact handler definitively rejected the node with retained evidence.</summary>
    Rejected,
    /// <summary>The exact handler stopped for durable review with retained ambiguity evidence.</summary>
    NeedsReview,
    /// <summary>The exact handler durably parked for an intentional human-review decision.</summary>
    ReviewPending,
    /// <summary>The request does not compose one guarded anchor, builder plan, exact node, and bounded attempt.</summary>
    InvalidRequest,
    /// <summary>No handler remains registered under the exact kind, type identifier, and version.</summary>
    UnsupportedDescriptor,
    /// <summary>The selected handler returned an invalid or unbound result.</summary>
    InvalidHandlerResult,
}
