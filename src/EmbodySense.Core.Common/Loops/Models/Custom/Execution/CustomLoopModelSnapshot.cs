namespace EmbodySense.Core.Common.Loops.Models.Custom.Execution;

/// <summary>
/// Represents a custom loop model snapshot.
/// </summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
public sealed record CustomLoopModelSnapshot(
    string Provider,
    string? Model);
