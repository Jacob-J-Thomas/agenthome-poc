namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Classifies one explicit request to start canonical governed-loop background delivery.</summary>
public enum AgentRuntimeGovernedLoopBackgroundStartStatus
{
    /// <summary>This runtime acquired the fenced coordinator lease and entered ready posture.</summary>
    Started = 1,

    /// <summary>This runtime already owns a ready coordinator and no duplicate dispatcher was created.</summary>
    AlreadyRunning = 2,

    /// <summary>Another live process owns delivery; this runtime must not take over or duplicate dispatch.</summary>
    OwnedByLivePeer = 3,

    /// <summary>A recoverable dependency or acquisition condition prevented safe startup.</summary>
    Unavailable = 4,

    /// <summary>Durable evidence requires explicit repair before background work can start.</summary>
    RepairRequired = 5,
}
