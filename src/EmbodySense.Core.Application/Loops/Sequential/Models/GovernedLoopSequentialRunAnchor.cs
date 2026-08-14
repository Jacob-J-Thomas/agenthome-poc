using EmbodySense.Core.Common.Loops.Sequential.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Models;

/// <summary>Represents a guard-issued proof that exact admission, invocation, graph, workspace, and run coordinates compose.</summary>
public sealed class GovernedLoopSequentialRunAnchor
{
    internal GovernedLoopSequentialRunAnchor(
        GovernedLoopSequentialAdapterBinding adapterBinding,
        GovernedLoopSequentialInvocationSnapshot invocationSnapshot)
    {
        AdapterBinding = adapterBinding;
        InvocationSnapshot = invocationSnapshot;
    }

    /// <summary>Gets the exact hash-bound adapter hand-off.</summary>
    public GovernedLoopSequentialAdapterBinding AdapterBinding { get; }

    /// <summary>Gets the exact hash-bound immutable invocation payload.</summary>
    public GovernedLoopSequentialInvocationSnapshot InvocationSnapshot { get; }
}
