namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Classifies one explicit request to stop canonical governed-loop background delivery.</summary>
public enum AgentRuntimeGovernedLoopBackgroundStopStatus
{
    /// <summary>Local admission stopped and all owned work reached a durable safe boundary.</summary>
    Stopped = 1,

    /// <summary>This runtime had no active local coordinator to stop.</summary>
    AlreadyStopped = 2,

    /// <summary>The fixed drain bound elapsed; ownership and durable evidence remain intact while callers continue polling.</summary>
    Draining = 3,

    /// <summary>Another live process owns the coordinator, so this runtime did not attempt to stop it.</summary>
    OwnedByLivePeer = 4,

    /// <summary>Durable ownership changed while this runtime was stopping.</summary>
    OwnershipLost = 5,

    /// <summary>A required dependency could not safely complete the stop request.</summary>
    Unavailable = 6,

    /// <summary>The coordinator failed closed before recording a normal stopped transition.</summary>
    Failed = 7,
}
