namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopContextPolicy(
    LoopContextInputPolicy ContextIn,
    LoopContextOutputPolicy ContextOut);
