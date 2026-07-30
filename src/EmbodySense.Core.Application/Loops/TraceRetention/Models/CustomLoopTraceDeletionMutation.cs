namespace EmbodySense.Core.Application.Loops.TraceRetention.Models;

public sealed record CustomLoopTraceDeletionMutation(
    CustomLoopTraceDeletionRequest Request,
    string RequestHash,
    DateTimeOffset RequestedAtUtc);
