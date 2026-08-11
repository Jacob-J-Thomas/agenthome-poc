namespace EmbodySense.Core.Application.Loops.Admission.Models;

/// <summary>Identifies one fail-closed admission-store lookup disposition.</summary>
public enum GovernedLoopAdmissionStoreReadStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact workspace and operation have no retained terminal outcome.</summary>
    NotFound = 1,

    /// <summary>The exact workspace and operation retain the returned immutable terminal outcome.</summary>
    Found = 2,

    /// <summary>The store could not provide a trustworthy observation and published no durable intent.</summary>
    Unavailable = 3,

    /// <summary>Available evidence cannot prove one consistent lookup result.</summary>
    Ambiguous = 4
}
