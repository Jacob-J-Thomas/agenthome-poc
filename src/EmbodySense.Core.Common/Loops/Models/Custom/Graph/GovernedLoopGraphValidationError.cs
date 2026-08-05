namespace EmbodySense.Core.Common.Loops.Models.Custom.Graph;

/// <summary>Describes one deterministic, bounded, element-attributed graph validation failure.</summary>
/// <param name="Code">The stable machine-readable error code.</param>
/// <param name="Element">The exact element reference.</param>
/// <param name="Message">The bounded human-readable explanation.</param>
public sealed record GovernedLoopGraphValidationError(string Code, GovernedLoopGraphElementReference Element, string Message);
