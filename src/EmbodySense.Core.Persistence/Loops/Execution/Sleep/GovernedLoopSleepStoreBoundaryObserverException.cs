namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

internal sealed class GovernedLoopSleepStoreBoundaryObserverException : Exception
{
    public GovernedLoopSleepStoreBoundaryObserverException(Exception innerException)
        : base("The sleep-store durability observer interrupted a publication boundary.", innerException)
    {
    }
}
