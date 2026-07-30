namespace EmbodySense.Core.Common.Loops.Models.Custom;

/// <summary>
/// Represents a custom loop context policy.
/// </summary>
/// <param name="ContextIn">The context in.</param>
/// <param name="ContextOut">The context out.</param>
public sealed record CustomLoopContextPolicy(
    CustomLoopContextInputPolicy ContextIn,
    CustomLoopContextOutputPolicy ContextOut);
