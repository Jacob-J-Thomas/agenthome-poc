namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Identifies the immutable provider and model captured for a run.
/// </summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
public sealed record LoopRunModelSnapshot(string Provider, string? Model);
