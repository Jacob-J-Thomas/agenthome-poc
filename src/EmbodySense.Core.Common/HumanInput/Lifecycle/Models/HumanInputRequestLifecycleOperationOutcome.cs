namespace EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

/// <summary>Identifies one immutable terminal lifecycle operation disposition.</summary>
public enum HumanInputRequestLifecycleOperationOutcome
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact lifecycle mutation committed.</summary>
    Committed = 1,
    /// <summary>Exact optimistic, lifecycle, candidate, or timing state conflicted.</summary>
    Conflict = 2,
    /// <summary>The exact target request lifecycle did not exist.</summary>
    NotFound = 3,
    /// <summary>A finite schema-1 lifecycle or persistence bound was exhausted.</summary>
    LimitExceeded = 4
}
