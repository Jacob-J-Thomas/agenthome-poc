namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

/// <summary>Names a durable boundary in one reconciliation case publication.</summary>
/// <remarks>
/// The boundary is observable only when a caller explicitly supplies an observer. It exists so a process-loss
/// fixture can terminate after a durable journal, case, effect successor, or receipt publication; normal callers
/// leave the observer unset and never alter the publication protocol.
/// </remarks>
public enum GovernedLoopEffectReconciliationPersistenceBoundary
{
    /// <summary>The interrupted transaction journal has been durably published.</summary>
    JournalPublished = 1,

    /// <summary>The immutable reconciliation case version and head have been durably published.</summary>
    CasePublished = 2,

    /// <summary>The proof-backed effect-attempt successor has been durably published.</summary>
    EffectPublished = 3,

    /// <summary>The immutable operation receipt has been durably published.</summary>
    ReceiptPublished = 4,
}
