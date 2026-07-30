namespace EmbodySense.Core.Startup.Loops.Execution.Models;

/// <summary>
/// Reports the durable outcome of an idempotent run lifecycle operation.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Run">The run.</param>
/// <param name="OperationId">The operation identifier.</param>
/// <param name="Detail">The detail.</param>
public sealed record LoopRunControlResponse(
    string Status,
    LoopRunSnapshot? Run,
    string OperationId,
    string Detail);
