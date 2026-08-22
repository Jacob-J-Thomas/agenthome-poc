namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>Defines one executable node declaration in a canonical governed graph.</summary>
/// <param name="Id">The stable node identifier.</param>
/// <param name="Descriptor">The extensible descriptor.</param>
/// <param name="Ports">The node-local typed ports.</param>
/// <param name="AuthorityCeiling">The non-granting node ceiling, which must narrow the loop ceiling.</param>
/// <param name="Parameters">The bounded descriptor-specific executable parameters.</param>
/// <param name="ModelRoutingPolicy">The optional typed Inference-node routing override.</param>
/// <param name="AuthoredInputDataClasses">The optional exact authored Inference input classification.</param>
public sealed record GovernedLoopNodeDefinition(
    string Id,
    GovernedLoopNodeDescriptor Descriptor,
    IReadOnlyList<GovernedLoopPortDefinition> Ports,
    GovernedLoopAuthorityCeiling AuthorityCeiling,
    IReadOnlyDictionary<string, string> Parameters,
    GovernedModelRoutingPolicy? ModelRoutingPolicy = null,
    IReadOnlyList<CapabilityDataClass>? AuthoredInputDataClasses = null)
{
    /// <summary>Gets an exact optional Inference-node routing override.</summary>
    public GovernedModelRoutingPolicy? ModelRoutingPolicy { get; } = ModelRoutingPolicy;

    /// <summary>Gets exact authored input classes, or null when classification is not authored.</summary>
    public IReadOnlyList<CapabilityDataClass>? AuthoredInputDataClasses { get; } = AuthoredInputDataClasses is null
        ? null
        : Array.AsReadOnly(AuthoredInputDataClasses.Take(CapabilityContractLimits.MaxDataClasses + 1).ToArray());
}
