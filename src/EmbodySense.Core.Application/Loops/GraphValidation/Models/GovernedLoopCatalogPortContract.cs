using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation.Models;

/// <summary>Describes one exact catalog-owned port contract for a node descriptor.</summary>
/// <param name="Id">The node-local port identity.</param>
/// <param name="Direction">The required direction.</param>
/// <param name="BindingKind">The required data or context channel.</param>
/// <param name="ValueKind">The required portable value kind.</param>
/// <param name="Required">Whether the graph port must be required.</param>
public sealed record GovernedLoopCatalogPortContract(string Id, GovernedLoopPortDirection Direction, GovernedLoopBindingKind BindingKind, GovernedLoopValueKind ValueKind, bool Required);
