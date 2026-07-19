namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public sealed record CustomLoopOrderedRunRequest(
    string RunId,
    string Actor);
