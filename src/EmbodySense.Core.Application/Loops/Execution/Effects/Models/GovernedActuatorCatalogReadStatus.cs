namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Identifies whether a bounded deterministic actuator-operation page could be proved current.</summary>
public enum GovernedActuatorCatalogReadStatus
{
    /// <summary>No supported status was selected.</summary>
    Unknown = 0,

    /// <summary>The bounded active server-backed catalog was read from current authoritative lifecycle state.</summary>
    Available = 1,

    /// <summary>Current capability lifecycle state was unavailable or ambiguous.</summary>
    Unavailable = 2,

    /// <summary>The requested bound was malformed.</summary>
    InvalidRequest = 3,

    /// <summary>More active operations existed than the caller's explicit bound.</summary>
    Truncated = 4,
}
