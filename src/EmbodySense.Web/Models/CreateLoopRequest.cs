namespace EmbodySense.Web.Models;

/// <summary>
/// Represents the idempotency identity for one custom-loop create request.
/// </summary>
/// <param name="OperationId">The caller-generated operation identifier reused after ambiguous outcomes.</param>
public sealed record CreateLoopRequest(string OperationId);
