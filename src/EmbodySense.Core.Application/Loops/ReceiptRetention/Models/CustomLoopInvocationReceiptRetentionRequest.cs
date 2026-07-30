namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents a custom loop invocation receipt retention request.
/// </summary>
/// <param name="OperationId">The operation ID.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
/// <param name="ReplayCutoffUtc">The replay cutoff UTC.</param>
public sealed record CustomLoopInvocationReceiptRetentionRequest(
    string OperationId,
    string Actor,
    string Surface,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReplayCutoffUtc);
