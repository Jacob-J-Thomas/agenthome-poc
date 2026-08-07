namespace EmbodySense.Core.Startup.Loops.Models;

/// <summary>
/// Projects workspace-wide custom-loop receipt-retention posture for an authenticated interface.
/// </summary>
/// <param name="GeneratedAtUtc">The UTC observation time.</param>
/// <param name="Health">The most severe actionable workspace health.</param>
/// <param name="Classes">The complete per-class posture set.</param>
/// <param name="ActiveCleanupJournalUtf8Bytes">The bytes held by the three active cleanup journals.</param>
/// <param name="AccountedWorkspaceUtf8Bytes">The total accounted raw evidence, compact proof, histories, and active journals.</param>
/// <param name="MaximumWorkspaceUtf8Bytes">The workspace-wide accounting ceiling.</param>
/// <param name="AvailableWorkspaceUtf8Bytes">The remaining workspace accounting capacity.</param>
/// <param name="ExhaustionReason">The workspace capacity reason, or <c>None</c>.</param>
/// <param name="CleanupBlockReason">The strongest workspace cleanup block reason, or <c>None</c>.</param>
/// <param name="Detail">A bounded actionable workspace detail.</param>
public sealed record LoopReceiptRetentionPostureSnapshot(
    DateTimeOffset GeneratedAtUtc,
    LoopReceiptRetentionHealth Health,
    IReadOnlyList<LoopReceiptRetentionClassSnapshot> Classes,
    long ActiveCleanupJournalUtf8Bytes,
    long AccountedWorkspaceUtf8Bytes,
    long MaximumWorkspaceUtf8Bytes,
    long AvailableWorkspaceUtf8Bytes,
    string ExhaustionReason,
    string CleanupBlockReason,
    string Detail);
