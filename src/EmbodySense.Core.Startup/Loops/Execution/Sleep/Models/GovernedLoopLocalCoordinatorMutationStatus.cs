namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

internal enum GovernedLoopLocalCoordinatorMutationStatus
{
    Succeeded,
    OwnershipLost,
    Conflict,
    Corrupt,
    Unavailable
}
