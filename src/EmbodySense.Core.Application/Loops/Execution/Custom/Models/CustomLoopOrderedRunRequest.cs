namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

/// <summary>
/// Represents a custom loop ordered run request.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="Actor">The actor.</param>
public sealed record CustomLoopOrderedRunRequest(
    string RunId,
    string Actor);
