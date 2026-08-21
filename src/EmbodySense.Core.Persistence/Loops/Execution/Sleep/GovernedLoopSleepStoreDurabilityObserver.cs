using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Persistence.Triggers;

namespace EmbodySense.Core.Persistence.Loops.Execution.Sleep;

internal sealed class GovernedLoopSleepStoreDurabilityObserver : ITriggerQueueDurabilityObserver
{
    private readonly Action<GovernedLoopSleepStorePersistenceBoundary>? _observer;

    public GovernedLoopSleepStoreDurabilityObserver(Action<GovernedLoopSleepStorePersistenceBoundary>? observer)
    {
        _observer = observer;
    }

    public void OnMutationDirectoryBound(string queueRoot)
    {
    }

    public void OnArtifactsObserved(string queueRoot)
    {
    }

    public void OnStagingDirectoryBound(long generation, string precursorPath, string destinationPath)
    {
    }

    public void OnStagingPrecursorCreated(long generation, string precursorPath, string destinationPath)
        => Notify(GovernedLoopSleepStorePersistenceBoundary.PrecursorCreated);

    public void OnStaged(long generation, string stagingPath, string destinationPath)
        => Notify(GovernedLoopSleepStorePersistenceBoundary.Staged);

    public void OnPublishing(long generation, string stagingPath, string destinationPath)
        => Notify(GovernedLoopSleepStorePersistenceBoundary.Publishing);

    public void OnPublishingDirectoryBound(long generation, string stagingPath, string destinationPath)
    {
    }

    public void OnPublished(long generation, string destinationPath)
        => Notify(GovernedLoopSleepStorePersistenceBoundary.Published);

    public void OnCleanupPrepared(long generation, string sourcePath, string claimPath)
    {
    }

    public void OnCleanupClaimed(long generation, string claimPath)
    {
    }

    public void OnCleanupDeleting(long generation, string claimPath)
    {
    }

    private void Notify(GovernedLoopSleepStorePersistenceBoundary boundary)
    {
        try
        {
            _observer?.Invoke(boundary);
        }
        catch (Exception exception) when (exception is not GovernedLoopSleepStoreBoundaryObserverException)
        {
            throw new GovernedLoopSleepStoreBoundaryObserverException(exception);
        }
    }
}
