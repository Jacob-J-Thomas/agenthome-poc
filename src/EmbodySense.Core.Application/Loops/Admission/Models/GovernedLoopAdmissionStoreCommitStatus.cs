namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Identifies one atomic governed-loop admission persistence disposition.</summary>
public enum GovernedLoopAdmissionStoreCommitStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact terminal outcome committed durably.</summary>
    Committed = 1,

    /// <summary>The exact workspace and operation already retained a terminal outcome.</summary>
    AlreadyCommitted = 2,

    /// <summary>The workspace-global operation identity is bound to different caller-stable intent.</summary>
    OperationConflict = 3,

    /// <summary>The optimistic workspace-global store generation changed before commit.</summary>
    GenerationConflict = 4,

    /// <summary>No durable intent began because the store was unavailable.</summary>
    Unavailable = 5,

    /// <summary>Available evidence cannot prove whether the exact outcome committed.</summary>
    Ambiguous = 6
}
