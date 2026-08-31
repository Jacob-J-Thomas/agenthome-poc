using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>Supplies current authenticated-operator authority for governed coordinator repair.</summary>
/// <remarks>
/// Implementations belong at an authenticated interface boundary and must derive the actor from current server-owned
/// request or session state. A caller-supplied actor identifier must never be treated as authority.
/// </remarks>
public interface IAgentRuntimeGovernedLoopCoordinatorRepairAuthorityProvider
{
    /// <summary>Reads authority for the current authenticated operator without accepting caller-owned identity.</summary>
    /// <param name="cancellationToken">A token that cancels authority resolution.</param>
    /// <returns>A current operator decision, or an unavailable decision when authentication cannot be established.</returns>
    Task<AgentRuntimeGovernedLoopCoordinatorRepairAuthority> ReadCurrentAsync(CancellationToken cancellationToken = default);
}
