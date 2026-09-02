namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

internal enum GovernedLoopEffectReconciliationJournalStage
{
    Pending = 1,
    CasePublished = 2,
    EffectPublished = 3,
    ReceiptPublished = 4
}
