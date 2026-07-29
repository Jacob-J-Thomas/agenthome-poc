namespace EmbodySense.Core.Application.Loops.Models;

public enum CustomLoopOperationIntegrity
{
    NotTracked = 1,
    PendingMutation = 2,
    PendingOutcomeAudit = 3,
    Complete = 4
}
