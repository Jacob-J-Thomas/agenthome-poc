namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

public sealed record CustomLoopTraceDeletionRequest(
    string RunId,
    string ExpectedTraceHash,
    string OperationId,
    string Actor,
    string Surface);
