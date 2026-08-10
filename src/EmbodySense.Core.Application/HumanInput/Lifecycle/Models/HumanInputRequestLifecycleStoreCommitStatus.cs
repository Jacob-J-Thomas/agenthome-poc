namespace EmbodySense.Core.Application.HumanInput.Lifecycle.Models;

/// <summary>Identifies one fail-closed Human Input lifecycle-store commit disposition.</summary>
public enum HumanInputRequestLifecycleStoreCommitStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The exact mutation committed durably.</summary>
    Committed = 1,
    /// <summary>The exact operation was already committed and replayed.</summary>
    Replayed = 2,
    /// <summary>The workspace-global generation changed before commit.</summary>
    StoreConflict = 3,
    /// <summary>The operation identifier is already bound to different intent.</summary>
    OperationConflict = 4,
    /// <summary>A finite schema-1 persistence bound was exhausted.</summary>
    LimitExceeded = 5,
    /// <summary>The store could not establish a durable result.</summary>
    Unavailable = 6,
    /// <summary>Available evidence cannot establish one safe result.</summary>
    Ambiguous = 7,
}
