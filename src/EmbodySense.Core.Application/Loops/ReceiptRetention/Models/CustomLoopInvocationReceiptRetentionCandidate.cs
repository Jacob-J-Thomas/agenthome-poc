namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

public sealed record CustomLoopInvocationReceiptRetentionCandidate(
    string OperationId,
    DateTimeOffset CompletedAtUtc,
    string ArtifactHash,
    long ArtifactUtf8Bytes);
