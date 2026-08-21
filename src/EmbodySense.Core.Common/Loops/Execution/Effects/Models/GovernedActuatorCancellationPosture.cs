namespace EmbodySense.Core.Common.Loops.Execution.Effects.Models;

/// <summary>Declares the cancellation guarantee around an actuator's irreversible boundary.</summary>
public enum GovernedActuatorCancellationPosture
{
    /// <summary>No supported posture was selected.</summary>
    Unknown = 0,

    /// <summary>Cancellation is conclusive only before the irreversible boundary.</summary>
    BeforeBoundaryOnly = 1,

    /// <summary>The adapter may cooperate after the boundary but cannot treat cancellation as proof of no effect.</summary>
    CooperativeAfterBoundary = 2,
}
