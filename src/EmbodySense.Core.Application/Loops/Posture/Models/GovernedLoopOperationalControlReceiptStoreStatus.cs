namespace EmbodySense.Core.Application.Loops.Posture.Models;

/// <summary>Identifies one closed durable control-receipt store outcome.</summary>
public enum GovernedLoopOperationalControlReceiptStoreStatus
{
    /// <summary>A new receipt was created or a compare-exchange successor committed.</summary>
    Committed = 1,

    /// <summary>The exact existing receipt was replayed.</summary>
    Replayed = 2,

    /// <summary>The operation identity or expected receipt hash conflicted.</summary>
    Conflict = 3,

    /// <summary>The operation is currently owned by another process.</summary>
    OperationInProgress = 4,

    /// <summary>Finite receipt capacity prevented the mutation.</summary>
    Backpressured = 5,

    /// <summary>Retained receipt evidence was malformed.</summary>
    Corrupt = 6,

    /// <summary>The durable receipt source was unavailable.</summary>
    Unavailable = 7
}
