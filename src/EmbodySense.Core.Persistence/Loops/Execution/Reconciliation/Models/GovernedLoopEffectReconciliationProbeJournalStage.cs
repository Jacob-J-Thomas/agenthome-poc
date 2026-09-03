namespace EmbodySense.Core.Persistence.Loops.Execution.Reconciliation.Models;

/// <summary>Durable stage of one probe observation append.</summary>
internal enum GovernedLoopEffectReconciliationProbeJournalStage
{
    Pending = 1,
    ReservationPublished = 2,
    ObservationPublished = 3,
    CasePublished = 4,
    ReceiptPublished = 5
}
