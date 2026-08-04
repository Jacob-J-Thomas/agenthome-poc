using System.Collections.Immutable;
using EmbodySense.Core.Common.Loops.Custom.Retention;
using EmbodySense.Core.Common.Loops.Models.Custom.Retention;

namespace EmbodySense.Core.Application.Loops.ReceiptRetention;

/// <summary>
/// Represents workspace-wide custom-loop authoring and lifecycle receipt-retention posture.
/// </summary>
/// <param name="GeneratedAtUtc">The posture observation timestamp.</param>
/// <param name="Classes">The complete class posture set.</param>
/// <param name="ActiveCleanupJournalUtf8Bytes">The bytes retained by active cleanup journals.</param>
/// <param name="ExhaustionReason">The workspace-wide exhaustion reason.</param>
/// <param name="CleanupBlockReason">The workspace-wide cleanup block reason.</param>
/// <param name="Detail">A bounded human-readable workspace detail.</param>
public sealed record CustomLoopReceiptRetentionPosture(
    DateTimeOffset GeneratedAtUtc,
    ImmutableArray<CustomLoopReceiptClassPosture> Classes,
    long ActiveCleanupJournalUtf8Bytes,
    CustomLoopReceiptQuotaExhaustionReason ExhaustionReason,
    CustomLoopReceiptCleanupBlockReason CleanupBlockReason,
    string Detail)
{
    /// <summary>
    /// Gets workspace-wide accounted bytes across raw artifacts, compact proof, and active journals.
    /// </summary>
    /// <value>The accounted workspace bytes.</value>
    public long AccountedWorkspaceUtf8Bytes => checked(Classes.Sum(item => item.AccountedUtf8Bytes) + ActiveCleanupJournalUtf8Bytes);

    /// <summary>
    /// Gets the workspace-wide byte ceiling.
    /// </summary>
    /// <value>The immutable accounted workspace byte ceiling.</value>
    public long MaximumWorkspaceUtf8Bytes => CustomLoopReceiptRetentionPolicy.MaxAccountedWorkspaceUtf8Bytes;

    /// <summary>
    /// Gets remaining workspace-wide accounted bytes.
    /// </summary>
    /// <value>Zero at or above the workspace ceiling; otherwise the remaining bytes.</value>
    public long AvailableWorkspaceUtf8Bytes => Math.Max(0, MaximumWorkspaceUtf8Bytes - AccountedWorkspaceUtf8Bytes);
}
