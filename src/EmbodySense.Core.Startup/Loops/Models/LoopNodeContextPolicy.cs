namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Selects inherited or explicit context behavior for one inference or exit node.
/// </summary>
/// <param name="Mode">Whether the node inherits its definition default or supplies a custom policy.</param>
/// <param name="CustomPolicy">The explicit policy required in custom mode and omitted in inherit mode.</param>
public sealed record LoopNodeContextPolicy(
    LoopContextPolicyMode Mode,
    LoopContextPolicy? CustomPolicy);
