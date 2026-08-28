namespace EmbodySense.Web.Models;

/// <summary>Tracks whether durable custom-loop recovery permits runtime admission.</summary>
internal enum WebLoopRecoveryLatch
{
    /// <summary>No recovery owns or blocks the retained runtime boundary.</summary>
    Idle = 0,

    /// <summary>One recovery attempt exclusively owns the retained runtime boundary.</summary>
    InProgress = 1,

    /// <summary>A prior recovery attempt did not complete and must be retried before runtime admission.</summary>
    Pending = 2,
}
