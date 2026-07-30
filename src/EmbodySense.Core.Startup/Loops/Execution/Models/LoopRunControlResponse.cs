namespace EmbodySense.Core.Startup.Loops.Execution.Models;

public sealed record LoopRunControlResponse(
    string Status,
    LoopRunSnapshot? Run,
    string OperationId,
    string Detail);
