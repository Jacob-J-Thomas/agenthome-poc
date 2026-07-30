namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents a custom loop invocation receipt retention candidate.
/// </summary>
/// <param name="OperationId">The operation ID.</param>
/// <param name="CompletedAtUtc">The UTC terminal time, or <see langword="null"/> while nonterminal.</param>
/// <param name="ArtifactHash">The artifact hash.</param>
/// <param name="ArtifactUtf8Bytes">The artifact UTF-8 bytes.</param>
public sealed record CustomLoopInvocationReceiptRetentionCandidate(
    string OperationId,
    DateTimeOffset CompletedAtUtc,
    string ArtifactHash,
    long ArtifactUtf8Bytes);
