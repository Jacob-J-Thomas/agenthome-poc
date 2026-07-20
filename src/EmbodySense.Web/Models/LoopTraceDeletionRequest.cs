namespace EmbodySense.Web.Models;

public sealed record LoopTraceDeletionRequest(string ExpectedTraceHash, string OperationId);
