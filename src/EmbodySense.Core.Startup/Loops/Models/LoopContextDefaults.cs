namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Defines the inherited context policies for inference and exit nodes.
/// </summary>
/// <param name="Inference">The policy inherited by inference nodes in inherit mode.</param>
/// <param name="Exit">The policy inherited by the exit node in inherit mode.</param>
public sealed record LoopContextDefaults(
    LoopContextPolicy Inference,
    LoopContextPolicy Exit);
