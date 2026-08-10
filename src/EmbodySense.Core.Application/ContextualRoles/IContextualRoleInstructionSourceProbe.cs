using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Common.ContextualRoles.Models;

namespace EmbodySense.Core.Application.ContextualRoles;

/// <summary>Probes one explicitly registered contextual-role instruction source without returning its content.</summary>
public interface IContextualRoleInstructionSourceProbe
{
    /// <summary>Validates the registered source's bounded physical posture.</summary>
    /// <param name="source">The classified opaque source reference.</param>
    /// <param name="cancellationToken">A token that cancels the probe before it completes.</param>
    /// <returns>A value-free source posture.</returns>
    Task<ContextualRoleInstructionSourceProbeResult> ProbeAsync(ContextualRoleInstructionSourceReference source, CancellationToken cancellationToken = default);
}
