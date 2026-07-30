namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

/// <summary>
/// Represents a custom loop trace deletion mutation.
/// </summary>
/// <param name="Request">The request.</param>
/// <param name="RequestHash">The request hash.</param>
/// <param name="RequestedAtUtc">The requested at UTC.</param>
public sealed record CustomLoopTraceDeletionMutation(
    CustomLoopTraceDeletionRequest Request,
    string RequestHash,
    DateTimeOffset RequestedAtUtc);
