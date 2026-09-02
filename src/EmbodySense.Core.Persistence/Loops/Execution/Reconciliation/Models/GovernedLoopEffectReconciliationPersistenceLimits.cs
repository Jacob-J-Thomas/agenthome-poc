namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

internal static class GovernedLoopEffectReconciliationPersistenceLimits
{
    internal const int MaximumCases = 4_096;
    internal const int MaximumCaseVersions = 64;
    internal const int MaximumOperationReceipts = 4_096;
    internal const int MaximumJournals = 64;
    internal const int MaximumJournalUtf8Bytes = 256 * 1024;
    internal const int MaximumReceiptUtf8Bytes = 16 * 1024;
    internal const int MaximumCursorBytes = 768;
    internal const string CaseFilePrefix = "reconciliation-case.";
    internal const string ReceiptFilePrefix = "reconciliation-receipt.";
    internal const string JournalFilePrefix = "reconciliation-journal.";
}
