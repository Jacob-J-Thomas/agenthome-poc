using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Application.Inference.Profiles;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests deterministic model routing for one reachable Inference node.</summary>
/// <param name="NodeId">The canonical node ID.</param>
/// <param name="NodeTypeId">The exact node implementation type ID.</param>
/// <param name="Policy">The effective loop-default or node-override policy.</param>
/// <param name="AuthoredInputDataClasses">Exact authoring-time input classes when the graph declares them; null means no classification was authored.</param>
public sealed record GovernedModelRoutingNodeAdmissionRequest(string NodeId, string NodeTypeId, GovernedModelRoutingPolicy Policy, IReadOnlyList<CapabilityDataClass>? AuthoredInputDataClasses)
{
    /// <summary>Gets a defensive canonical copy when authoring supplied classification.</summary>
    public IReadOnlyList<CapabilityDataClass>? AuthoredInputDataClasses { get; } = AuthoredInputDataClasses is null
        ? null
        : ModelProfileApplicationContractCopy.Snapshot(AuthoredInputDataClasses, CapabilityContractLimits.MaxDataClasses, nameof(AuthoredInputDataClasses));
}
