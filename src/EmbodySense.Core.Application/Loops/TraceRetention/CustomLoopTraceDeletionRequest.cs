namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionRequest(
    string RunId,
    string ExpectedTraceHash,
    string OperationId,
    string Actor,
    string Surface);
