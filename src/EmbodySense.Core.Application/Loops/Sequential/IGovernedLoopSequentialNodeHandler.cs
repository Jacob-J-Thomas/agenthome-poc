using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Handles exactly one admitted sequential node descriptor without owning traversal or durable advancement.</summary>
public interface IGovernedLoopSequentialNodeHandler
{
    /// <summary>Gets the exact kind, type identifier, and version handled by this port.</summary>
    GovernedLoopNodeDescriptor Descriptor { get; }

    /// <summary>Dispatches one exact guarded plan node and returns only already-retained evidence identity.</summary>
    Task<GovernedLoopSequentialNodeHandlerResult> DispatchAsync(
        GovernedLoopSequentialNodeDispatchRequest request,
        CancellationToken cancellationToken = default);
}
