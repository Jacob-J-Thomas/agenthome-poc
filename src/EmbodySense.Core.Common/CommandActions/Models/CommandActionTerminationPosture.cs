namespace EmbodySense.Core.Common.CommandActions.Models;

/// <summary>Identifies the exact process-tree terminal evidence behind a conclusive outcome.</summary>
public enum CommandActionTerminationPosture
{
    /// <summary>No terminal posture was proved.</summary>
    Unknown = 0,
    /// <summary>The process and admitted descendants exited without a termination request.</summary>
    Exited = 1,
    /// <summary>The isolation adapter proved the complete process tree terminal after termination was requested.</summary>
    ProcessTreeTerminated = 2,

    /// <summary>The isolation adapter proved that no process was created.</summary>
    NotStarted = 3,
}
