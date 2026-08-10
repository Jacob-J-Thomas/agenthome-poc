namespace EmbodySense.Core.Common.Loops.Execution.Models;

/// <summary>Identifies the aggregate posture of a canonical graph execution frontier.</summary>
public enum GovernedLoopFrontierStatus
{
    /// <summary>No supported frontier posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The frontier contains work that is ready or running.</summary>
    Active,
    /// <summary>The frontier is durably waiting.</summary>
    Waiting,
    /// <summary>The frontier is blocked on explicit review or reconciliation.</summary>
    ReviewBlocked,
    /// <summary>The frontier reached a successful terminal.</summary>
    Completed,
    /// <summary>The frontier reached a failed terminal.</summary>
    Failed,
    /// <summary>The frontier reached a cancelled terminal while retaining each node's last committed posture.</summary>
    Cancelled
}
