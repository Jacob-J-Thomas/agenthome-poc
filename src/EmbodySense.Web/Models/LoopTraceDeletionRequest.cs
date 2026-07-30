namespace EmbodySense.Web.Models;

/// <summary>
/// Represents an optimistic, idempotent request to delete retained trace content.
/// </summary>
/// <param name="ExpectedTraceHash">The exact trace-content hash the caller observed.</param>
/// <param name="OperationId">The caller-generated deletion-operation identity reused after ambiguous outcomes.</param>
public sealed record LoopTraceDeletionRequest(string ExpectedTraceHash, string OperationId);
