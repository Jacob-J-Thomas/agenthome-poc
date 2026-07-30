namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Represents a custom loop trace deletion request.
/// </summary>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="ExpectedTraceHash">The expected trace hash.</param>
/// <param name="OperationId">The operation ID.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
public sealed record CustomLoopTraceDeletionRequest(
    string RunId,
    string ExpectedTraceHash,
    string OperationId,
    string Actor,
    string Surface);
