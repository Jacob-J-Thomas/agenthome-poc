namespace EmbodySense.Web.Models;

/// <summary>
/// Represents an optimistic, idempotent custom-loop deletion request.
/// </summary>
/// <param name="ExpectedDefinitionVersion">The exact durable definition version the caller observed.</param>
/// <param name="OperationId">The caller-generated operation identifier reused after ambiguous outcomes.</param>
public sealed record DeleteLoopRequest(int ExpectedDefinitionVersion, string OperationId);
