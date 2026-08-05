namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Declares the meaning and typed outputs of successful loop completion.</summary>
/// <param name="Summary">The bounded canonical success contract.</param>
/// <param name="Outputs">The explicitly sourced typed outputs.</param>
public sealed record GovernedLoopOutputContract(string Summary, IReadOnlyList<GovernedLoopOutputDefinition> Outputs);
