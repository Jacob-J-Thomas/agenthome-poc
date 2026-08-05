using EmbodySense.Core.Application.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Reads the proved current lifecycle projection of a stable contextual role.</summary>
public interface IContextualRoleLifecycleReader
{
    /// <summary>Reads current lifecycle state without granting authority or loading referenced instructions.</summary>
    /// <param name="request">The stable role identity to read.</param>
    /// <param name="cancellationToken">A token that cancels the read before it completes.</param>
    /// <returns>The proved current lifecycle projection or a structured fail-closed outcome.</returns>
    Task<ContextualRoleLifecycleReadResult> ReadLifecycleAsync(ContextualRoleLifecycleReadRequest request, CancellationToken cancellationToken = default);
}
