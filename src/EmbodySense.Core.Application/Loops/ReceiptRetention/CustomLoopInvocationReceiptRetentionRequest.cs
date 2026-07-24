namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public sealed record CustomLoopInvocationReceiptRetentionRequest(
    string OperationId,
    string Actor,
    string Surface,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ReplayCutoffUtc);
