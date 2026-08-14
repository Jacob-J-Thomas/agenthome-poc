using EmbodySense.Core.Common.Loops.Models.Custom.Graph;
using EmbodySense.Core.Common.Loops.PureNodes;

namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Describes one exact catalog-owned port contract for a node descriptor.</summary>
/// <param name="Id">The node-local port identity.</param>
/// <param name="Direction">The required direction.</param>
/// <param name="BindingKind">The required data or context channel.</param>
/// <param name="AllowedValueKinds">The non-empty exact set of portable value kinds admitted by the port.</param>
/// <param name="Required">Whether the graph port must be required.</param>
public sealed record GovernedLoopCatalogPortContract(
    string Id,
    GovernedLoopPortDirection Direction,
    GovernedLoopBindingKind BindingKind,
    GovernedLoopValueKindSet AllowedValueKinds,
    bool Required);
