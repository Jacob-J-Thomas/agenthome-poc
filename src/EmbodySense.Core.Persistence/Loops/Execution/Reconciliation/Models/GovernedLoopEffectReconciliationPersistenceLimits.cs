namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

internal static class GovernedLoopEffectReconciliationPersistenceLimits
{
    internal const int MaximumCases = 4_096;
    internal const int MaximumCaseVersions = 64;
    internal const int MaximumOperationReceipts = 4_096;
    internal const int MaximumJournals = 64;
    internal const int MaximumJournalUtf8Bytes = 256 * 1024;
    internal const int MaximumReceiptUtf8Bytes = 16 * 1024;
    internal const int MaximumProbeReservations = 4_096;
    internal const int MaximumProbeReservationsPerCase = 32;
    internal const int MaximumProbeReservationUtf8Bytes = 64 * 1024;
    internal const int MaximumProbeObservations = 4_096;
    internal const int MaximumProbeObservationUtf8Bytes = 32 * 1024;
    internal const int MaximumProbeJournals = 64;
    internal const int MaximumProbeJournalUtf8Bytes = 256 * 1024;
    internal const int MaximumCursorBytes = 768;
    internal const string CaseFilePrefix = "reconciliation-case.";
    internal const string ReceiptFilePrefix = "reconciliation-receipt.";
    internal const string JournalFilePrefix = "reconciliation-journal.";
    internal const string ProbeReservationFilePrefix = "reconciliation-probe-reservation.";
    internal const string ProbeObservationFilePrefix = "reconciliation-probe-observation.";
    internal const string ProbeJournalFilePrefix = "reconciliation-probe-journal.";
}
