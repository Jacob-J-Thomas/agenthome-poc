namespace EmbodySense.Core.Application.Loops.Execution.Custom.Models;

public sealed record CustomLoopOrderedRunRequest(
    string RunId,
    string Actor);
