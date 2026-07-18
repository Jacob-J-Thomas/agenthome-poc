namespace EmbodySense.Core.Application.Loops;

public enum CustomLoopOperationIntegrity
{
    Unknown = 0,
    NotTracked = 1,
    PendingMutation = 2,
    PendingOutcomeAudit = 3,
    Complete = 4
}
