namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Combines a node's input composition and output retention/publication policy.
/// </summary>
/// <param name="ContextIn">The sources admitted to the node's model context.</param>
/// <param name="ContextOut">The allowed uses of the node's canonical output.</param>
public sealed record LoopContextPolicy(
    LoopContextInputPolicy ContextIn,
    LoopContextOutputPolicy ContextOut);
