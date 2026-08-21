namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Identifies whether a native workspace dispatch was not started or produced one observed outcome.</summary>
public enum WorkspaceActionNativeCommitStatus
{
    /// <summary>No supported status was selected.</summary>
    Unknown = 0,

    /// <summary>The native host proved that the native dispatch boundary was not crossed.</summary>
    DispatchNotStarted = 1,

    /// <summary>The durable boundary returned one conclusive native outcome.</summary>
    OutcomeObserved = 2,
}
