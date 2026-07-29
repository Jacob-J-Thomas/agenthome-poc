namespace EmbodySense.Core.Startup.Loops.Models;

public sealed record LoopContextDefaults(
    LoopContextPolicy Inference,
    LoopContextPolicy Exit);
