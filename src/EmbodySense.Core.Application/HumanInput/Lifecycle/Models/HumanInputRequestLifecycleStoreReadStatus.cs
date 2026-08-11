namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Identifies one fail-closed Human Input lifecycle-store read disposition.</summary>
public enum HumanInputRequestLifecycleStoreReadStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact target lifecycle was read successfully.</summary>
    Ready = 1,
    /// <summary>The exact target lifecycle does not exist.</summary>
    NotFound = 2,
    /// <summary>The operation identifier is already bound to different intent.</summary>
    OperationConflict = 3,
    /// <summary>The store could not be read safely.</summary>
    Unavailable = 4,
    /// <summary>Available evidence cannot establish one safe state.</summary>
    Ambiguous = 5,
}
