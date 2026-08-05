using EmbodySense.Core.Common.Loops.Custom.Graph;
using EmbodySense.Core.Application.Loops.GraphValidation.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

namespace EmbodySense.Core.Application.Loops.GraphValidation;

/// <summary>Returns a normalized executable graph only when structural, catalog, and current-authority validation all succeed.</summary>
/// <param name="Graph">The canonical graph on success, otherwise <see langword="null"/>.</param>
/// <param name="Evidence">The deterministic snapshot evidence when both snapshots were available and well formed.</param>
/// <param name="Errors">The bounded deterministic errors.</param>
public sealed record GovernedLoopGraphValidationResult(GovernedLoopGraphDefinition? Graph, GovernedLoopGraphValidationEvidence? Evidence, IReadOnlyList<GovernedLoopGraphValidationError> Errors)
{
    /// <summary>Gets whether the graph is currently valid and explicitly executable.</summary>
    /// <value><see langword="true"/> only when graph and evidence are available and errors are empty.</value>
    public bool IsValid => Graph is not null && Evidence is not null && Errors.Count == 0;
}
