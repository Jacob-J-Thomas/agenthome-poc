namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Identifies the closed non-response lifecycle posture of a durable Human Input request.</summary>
public enum HumanInputRequestLifecycleStatus
{
    /// <summary>No supported posture was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact current request version may collect untrusted response data.</summary>
    Pending = 1,
    /// <summary>An authenticated lifecycle actor rejected the request without submitting response data.</summary>
    Rejected = 2,
    /// <summary>An authenticated lifecycle actor cancelled the request.</summary>
    Cancelled = 3,
    /// <summary>Trusted time passed the inclusive response endpoint and the request was explicitly expired.</summary>
    Expired = 4,
    /// <summary>A different exact request replaced this request while retaining an explicit lineage link.</summary>
    Superseded = 5
}
