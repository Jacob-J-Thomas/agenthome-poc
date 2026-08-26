namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Classifies the active ownership visible through the Startup background-runtime boundary.</summary>
public enum AgentRuntimeGovernedLoopBackgroundOwnership
{
    /// <summary>This <see cref="AgentRuntime"/> owns the active canonical coordinator.</summary>
    Local = 1,

    /// <summary>Another live process retains the exclusive coordinator lease.</summary>
    LivePeer = 2,

    /// <summary>No active coordinator ownership exists.</summary>
    None = 3,

    /// <summary>Durable ownership cannot be safely classified.</summary>
    Unknown = 4,
}
