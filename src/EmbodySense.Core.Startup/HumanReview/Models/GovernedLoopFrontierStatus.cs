namespace EmbodySense.Core.Startup.HumanReview.Models;

/// <summary>Identifies the detached posture of a canonical graph execution frontier.</summary>
public enum GovernedLoopFrontierStatus
{
    /// <summary>No supported frontier posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The frontier contains ready or running work.</summary>
    Active = 1,
    /// <summary>The frontier is durably waiting.</summary>
    Waiting = 2,
    /// <summary>The frontier is blocked on explicit review or reconciliation.</summary>
    ReviewBlocked = 3,
    /// <summary>The frontier reached a successful terminal.</summary>
    Completed = 4,
    /// <summary>The frontier reached a failed terminal.</summary>
    Failed = 5,
    /// <summary>The frontier reached a cancelled terminal.</summary>
    Cancelled = 6
}
