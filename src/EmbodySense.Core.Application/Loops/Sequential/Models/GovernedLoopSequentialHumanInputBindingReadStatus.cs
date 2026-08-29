namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Classifies one exact Human Input value rehydration without collapsing dependency outage into divergent evidence.</summary>
public enum GovernedLoopSequentialHumanInputBindingReadStatus
{
    /// <summary>No supported disposition was supplied.</summary>
    Unknown = 0,

    /// <summary>The exact selected response value was rehydrated and bound safely.</summary>
    Ready = 1,

    /// <summary>The canonical response source could not be read safely; callers must leave the run unchanged for retry.</summary>
    Unavailable = 2,

    /// <summary>The retained checkpoint, answered lifecycle state, selection, response value, or projection was invalid or divergent.</summary>
    Invalid = 3,
}
