namespace EmbodySense.Core.Application.HumanInput.Responses.Models;

/// <summary>Identifies one fail-closed response-store read disposition.</summary>
public enum HumanInputResponseLifecycleStoreReadStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,
    /// <summary>The request and its bounded response state were read safely.</summary>
    Ready = 1,
    /// <summary>The exact target request does not exist.</summary>
    NotFound = 2,
    /// <summary>The operation ID is already bound to changed or foreign-family intent.</summary>
    OperationConflict = 3,
    /// <summary>The store could not be read safely.</summary>
    Unavailable = 4,
    /// <summary>Available evidence cannot establish one safe state.</summary>
    Ambiguous = 5,
}
