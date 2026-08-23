using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests exact model-routing evidence from immutable pre-receipt admission evidence.</summary>
/// <param name="Seed">The complete non-circular admission seed.</param>
/// <param name="Nodes">Reachable Inference nodes ordered by node ID.</param>
public sealed record GovernedModelRoutingAdmissionRequest(GovernedModelRoutingAdmissionSeed Seed, IReadOnlyList<GovernedModelRoutingNodeAdmissionRequest> Nodes)
{
    /// <summary>Gets a defensive copy of node requests.</summary>
    public IReadOnlyList<GovernedModelRoutingNodeAdmissionRequest> Nodes { get; } = ModelProfileApplicationContractCopy.Snapshot(Nodes, GovernedModelContractLimits.MaxAdmissionEntries, nameof(Nodes));
}
