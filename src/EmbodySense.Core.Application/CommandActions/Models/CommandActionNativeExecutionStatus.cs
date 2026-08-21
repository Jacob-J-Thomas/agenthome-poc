namespace EmbodySense.Core.Application.CommandActions.Models;

/// <summary>Identifies whether the native host proved no launch boundary or observed a conclusive outcome.</summary>
public enum CommandActionNativeExecutionStatus
{
    /// <summary>The host proved the irreversible launch boundary was not requested.</summary>
    DispatchNotStarted = 1,
    /// <summary>The launch boundary returned a conclusive retained outcome.</summary>
    OutcomeObserved = 2,
}
