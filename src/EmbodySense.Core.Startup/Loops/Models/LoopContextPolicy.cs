namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopContextPolicy(
    LoopContextInputPolicy ContextIn,
    LoopContextOutputPolicy ContextOut);
