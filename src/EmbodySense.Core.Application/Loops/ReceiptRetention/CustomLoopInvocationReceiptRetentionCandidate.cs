namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

public sealed record CustomLoopInvocationReceiptRetentionCandidate(
    string OperationId,
    DateTimeOffset CompletedAtUtc,
    string ArtifactHash,
    long ArtifactUtf8Bytes);
