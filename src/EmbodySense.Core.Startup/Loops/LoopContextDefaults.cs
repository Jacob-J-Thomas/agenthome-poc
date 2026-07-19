namespace EmbodySense.Core.Startup.Loops;

public sealed record LoopContextDefaults(
    LoopContextPolicy Inference,
    LoopContextPolicy Exit);
