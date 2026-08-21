namespace EmbodySense.Core.Application.Loops.Execution.Effects.Models;

/// <summary>Identifies whether a structured adapter proved no dispatch or observed a conclusive external outcome.</summary>
public enum GovernedActuatorAdapterStatus
{
    /// <summary>No supported status was selected.</summary>
    Unknown = 0,

    /// <summary>The adapter affirmatively proved that its irreversible boundary was never crossed.</summary>
    DispatchNotStarted = 1,

    /// <summary>The service-owned boundary returned one conclusive external outcome.</summary>
    OutcomeObserved = 2,
}
