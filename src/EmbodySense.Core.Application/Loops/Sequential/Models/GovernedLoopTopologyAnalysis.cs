namespace EmbodySense.Core.Application.Loops.Sequential.Models;

internal sealed class GovernedLoopTopologyAnalysis
{
    internal GovernedLoopTopologyAnalysis(
        IReadOnlyList<GovernedLoopTopologyComponent> components,
        IReadOnlyDictionary<string, GovernedLoopTopologyComponent> componentByNodeId)
    {
        Components = components;
        ComponentByNodeId = componentByNodeId;
    }

    internal IReadOnlyList<GovernedLoopTopologyComponent> Components { get; }
    internal IReadOnlyDictionary<string, GovernedLoopTopologyComponent> ComponentByNodeId { get; }
}
