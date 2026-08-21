namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Identifies exact current catalog and server-registration posture for one admitted actuator operation.</summary>
public enum GovernedActuatorCatalogResolutionStatus
{
    /// <summary>No supported result was selected.</summary>
    Unknown = 0,

    /// <summary>The exact admitted pin is active and backed by one exact server registration.</summary>
    Active = 1,

    /// <summary>The request or admitted pin was malformed.</summary>
    InvalidRequest = 2,

    /// <summary>No trustworthy current capability catalog could be read.</summary>
    CatalogUnavailable = 3,

    /// <summary>The catalog changed during bounded resolution or contained duplicate truth.</summary>
    CatalogAmbiguous = 4,

    /// <summary>The admitted capability id is no longer present.</summary>
    PinMissing = 5,

    /// <summary>The current descriptor or implementation no longer matches the exact admitted pin.</summary>
    PinDrifted = 6,

    /// <summary>The exact capability is not currently installed, enabled, healthy, trusted, compatible, and active.</summary>
    PinInactive = 7,

    /// <summary>No exact server adapter registration backs the admitted operation.</summary>
    OperationUnregistered = 8,
}
