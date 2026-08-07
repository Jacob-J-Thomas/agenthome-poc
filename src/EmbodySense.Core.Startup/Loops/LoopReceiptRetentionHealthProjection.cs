using EmbodySense.Core.Startup.Loops.Models;

namespace EmbodySense.Core.Startup.Loops;

/// <summary>
/// Classifies safe receipt-retention posture and cleanup outcomes for interface hosts.
/// </summary>
public static class LoopReceiptRetentionHealthProjection
{
    /// <summary>
    /// Classifies a posture from its safe exhaustion and cleanup-block names.
    /// </summary>
    /// <param name="exhaustionReason">The safe exhaustion-reason name.</param>
    /// <param name="cleanupBlockReason">The safe cleanup-block name.</param>
    /// <returns>A fail-closed actionable health.</returns>
    public static LoopReceiptRetentionHealth FromPosture(string exhaustionReason, string cleanupBlockReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exhaustionReason);
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupBlockReason);

        return cleanupBlockReason switch
        {
            "None" => exhaustionReason == "None" ? LoopReceiptRetentionHealth.Healthy : LoopReceiptRetentionHealth.Exhausted,
            "CorruptEvidence" => LoopReceiptRetentionHealth.Corrupt,
            "AuditUnavailable" => LoopReceiptRetentionHealth.AuditUnavailable,
            "OwnershipUnresolved" => LoopReceiptRetentionHealth.RecoveryPending,
            "ProofCapacityExhausted" or "CleanupHistoryCapacityExhausted" => LoopReceiptRetentionHealth.Exhausted,
            _ => LoopReceiptRetentionHealth.Degraded
        };
    }

    /// <summary>
    /// Classifies a cleanup response without losing stronger fail-closed posture evidence.
    /// </summary>
    /// <param name="status">The safe cleanup-status name.</param>
    /// <param name="exhaustionReason">The safe exhaustion-reason name.</param>
    /// <param name="cleanupBlockReason">The safe cleanup-block name.</param>
    /// <returns>A fail-closed actionable health.</returns>
    public static LoopReceiptRetentionHealth FromCleanup(string status, string exhaustionReason, string cleanupBlockReason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        var statusHealth = status switch
        {
            "OperationInProgress" => LoopReceiptRetentionHealth.OwnershipConflict,
            "AuditUnavailable" or "CommittedWithAuditWarning" => LoopReceiptRetentionHealth.AuditUnavailable,
            "Corrupt" => LoopReceiptRetentionHealth.Corrupt,
            "Degraded" or "CleanupConflict" or "Invalid" or "Unknown" => LoopReceiptRetentionHealth.Degraded,
            "QuotaExhausted" => LoopReceiptRetentionHealth.Exhausted,
            _ => LoopReceiptRetentionHealth.Healthy
        };
        var postureHealth = FromPosture(exhaustionReason, cleanupBlockReason);
        return Severity(statusHealth) >= Severity(postureHealth) ? statusHealth : postureHealth;
    }

    /// <summary>
    /// Selects the deterministic cleanup-block reason aligned with the most severe class posture.
    /// </summary>
    /// <param name="classes">The safe per-class posture set.</param>
    /// <returns>The strongest concrete cleanup-block reason, or <c>None</c>.</returns>
    public static string SelectWorkspaceCleanupBlockReason(IEnumerable<LoopReceiptRetentionClassSnapshot> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        return classes
            .Where(item => item.CleanupBlockReason != "None")
            .OrderByDescending(item => Severity(item.Health))
            .ThenByDescending(item => BlockSeverity(item.CleanupBlockReason))
            .ThenBy(item => item.ArtifactClass, StringComparer.Ordinal)
            .Select(item => item.CleanupBlockReason)
            .FirstOrDefault() ?? "None";
    }

    private static int Severity(LoopReceiptRetentionHealth health)
    {
        return health switch
        {
            LoopReceiptRetentionHealth.Corrupt => 7,
            LoopReceiptRetentionHealth.AuditUnavailable => 6,
            LoopReceiptRetentionHealth.OwnershipConflict => 5,
            LoopReceiptRetentionHealth.RecoveryPending => 4,
            LoopReceiptRetentionHealth.Degraded => 3,
            LoopReceiptRetentionHealth.Exhausted => 2,
            _ => 1
        };
    }

    private static int BlockSeverity(string blockReason)
    {
        return blockReason switch
        {
            "CorruptEvidence" => 11,
            "AuditUnavailable" => 10,
            "OwnershipUnresolved" => 9,
            "CleanupConflict" => 8,
            "AmbiguousEvidence" => 7,
            "DegradedEvidence" => 6,
            "UnauditedEvidence" => 5,
            "PendingEvidence" => 4,
            "CleanupHistoryCapacityExhausted" => 3,
            "ProofCapacityExhausted" => 2,
            _ => 1
        };
    }
}
