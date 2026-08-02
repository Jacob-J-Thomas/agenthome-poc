namespace EmbodySense.Core.Application.Capabilities.Models;

/// <summary>Identifies whether a terminal lifecycle receipt was marked audited.</summary>
public enum CapabilityLifecycleAuditMarkStatus
{
    /// <summary>The pending marker was durably cleared.</summary>
    Applied = 1,
    /// <summary>The receipt was already marked audited.</summary>
    NoChange = 2,
    /// <summary>No terminal receipt exists for the operation.</summary>
    NotFound = 3,
    /// <summary>The marker could not be proved safely.</summary>
    Unavailable = 4
}
