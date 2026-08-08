namespace EmbodySense.Core.Application.Loops.ReceiptRetention.Models;

/// <summary>
/// Represents the safe accounting and state summary for one class-specific active cleanup journal.
/// </summary>
/// <param name="Utf8Bytes">The canonical serialized journal bytes, or zero when no journal exists.</param>
/// <param name="Stage">The current durable stage, or <see langword="null"/> when no journal exists.</param>
/// <param name="Outcome">The current durable outcome, or <see langword="null"/> when no journal exists.</param>
/// <param name="RecoveryAvailableAtUtc">The earliest safe explicit recovery retry, or <see langword="null"/> when no active recovery exists.</param>
public sealed record CustomLoopReceiptActiveCleanupJournalPosture(
    long Utf8Bytes,
    CustomLoopReceiptCleanupStage? Stage,
    CustomLoopReceiptCleanupOutcome? Outcome,
    DateTimeOffset? RecoveryAvailableAtUtc);
