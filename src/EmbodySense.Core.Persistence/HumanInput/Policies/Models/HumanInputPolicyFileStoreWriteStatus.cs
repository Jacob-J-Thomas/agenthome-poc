namespace EmbodySense.Core.Persistence.HumanInput.Policies.Models;

/// <summary>Identifies the closed outcome of a strict optimistic immutable Human Input policy write.</summary>
public enum HumanInputPolicyFileStoreWriteStatus
{
    /// <summary>No supported write outcome was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact immutable artifact was persisted.</summary>
    Committed = 1,

    /// <summary>The exact immutable artifact was already persisted.</summary>
    Replayed = 2,

    /// <summary>The caller's expected generation is stale.</summary>
    Conflict = 3,

    /// <summary>The artifact conflicts with an existing immutable policy revision or is malformed.</summary>
    Invalid = 4,

    /// <summary>The store could not safely prove the durable outcome.</summary>
    Unavailable = 5,
}
