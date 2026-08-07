namespace EmbodySense.Core.Common.Loops.Custom.Graph;

using EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Returns a canonical graph only when raw normalization and structural validation succeed.</summary>
/// <param name="Graph">The canonical immutable graph on success, otherwise <see langword="null"/>.</param>
/// <param name="Errors">The bounded deterministic validation errors.</param>
public sealed record GovernedLoopGraphNormalizationResult(GovernedLoopGraphDefinition? Graph, IReadOnlyList<GovernedLoopGraphValidationError> Errors)
{
    /// <summary>Gets whether normalization succeeded without errors.</summary>
    /// <value><see langword="true"/> only when <see cref="Graph"/> is available and <see cref="Errors"/> is empty.</value>
    public bool IsValid => Graph is not null && Errors.Count == 0;
}
