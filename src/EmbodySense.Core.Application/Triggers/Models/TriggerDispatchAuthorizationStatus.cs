namespace EmbodySense.Core.Application.Triggers.Models;

/// <summary>Identifies current-evidence revalidation immediately before dispatch intent.</summary>
public enum TriggerDispatchAuthorizationStatus
{
    /// <summary>All current loop, assignment, capability, authority, and temporal evidence permits dispatch.</summary>
    Authorized,

    /// <summary>Current evidence proves dispatch is not authorized.</summary>
    Rejected,

    /// <summary>Current evidence could not be loaded or proved safely.</summary>
    Unavailable
}
