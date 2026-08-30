namespace EmbodySense.Core.Startup.Runtime.Models;

/// <summary>Describes whether the authenticated interface boundary permits coordinator repair for its current operator.</summary>
public enum AgentRuntimeGovernedLoopCoordinatorRepairAuthorityStatus
{
    /// <summary>The current authenticated operator may inspect and submit coordinator repair.</summary>
    Ready = 0,

    /// <summary>The current authenticated operator is known but is not authorized for coordinator repair.</summary>
    Denied = 1,

    /// <summary>The interface boundary could not establish current authenticated operator authority.</summary>
    Unavailable = 2
}
