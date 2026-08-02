namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Defines one executable node declaration in a canonical governed graph.</summary>
/// <param name="Id">The stable node identifier.</param>
/// <param name="Descriptor">The extensible descriptor.</param>
/// <param name="Ports">The node-local typed ports.</param>
/// <param name="AuthorityCeiling">The non-granting node ceiling, which must narrow the loop ceiling.</param>
/// <param name="Parameters">The bounded descriptor-specific executable parameters.</param>
public sealed record GovernedLoopNodeDefinition(
    string Id,
    GovernedLoopNodeDescriptor Descriptor,
    IReadOnlyList<GovernedLoopPortDefinition> Ports,
    GovernedLoopAuthorityCeiling AuthorityCeiling,
    IReadOnlyDictionary<string, string> Parameters);
