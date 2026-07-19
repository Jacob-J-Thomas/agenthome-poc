namespace EmbodySense.Core.Application.Loops.TraceRetention;

public sealed record CustomLoopTraceDeletionMutation(
    CustomLoopTraceDeletionRequest Request,
    string RequestHash,
    DateTimeOffset RequestedAtUtc);
