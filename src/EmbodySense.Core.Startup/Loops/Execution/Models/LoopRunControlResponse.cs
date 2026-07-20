namespace EmbodySense.Core.Startup.Loops.Execution;

public sealed record LoopRunControlResponse(
    string Status,
    LoopRunSnapshot? Run,
    string OperationId,
    string Detail);
