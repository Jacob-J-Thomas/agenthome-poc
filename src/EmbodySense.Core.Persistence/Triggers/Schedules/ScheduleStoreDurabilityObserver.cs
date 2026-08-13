using EmbodySense.Core.Persistence.Triggers.Schedules.Models;

namespace EmbodySense.Core.Persistence.Triggers.Schedules;

/// <summary>Projects the hardened immutable-generation protocol onto schedule-specific crash boundaries.</summary>
internal sealed class ScheduleStoreDurabilityObserver : ITriggerQueueDurabilityObserver
{
    private readonly Action<ScheduleStorePersistenceBoundary>? _observer;

    public ScheduleStoreDurabilityObserver(Action<ScheduleStorePersistenceBoundary>? observer)
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
        => Notify(ScheduleStorePersistenceBoundary.PrecursorCreated);

    public void OnStaged(long generation, string stagingPath, string destinationPath)
        => Notify(ScheduleStorePersistenceBoundary.Staged);

    public void OnPublishing(long generation, string stagingPath, string destinationPath)
        => Notify(ScheduleStorePersistenceBoundary.Publishing);

    public void OnPublishingDirectoryBound(long generation, string stagingPath, string destinationPath)
    {
    }

    public void OnPublished(long generation, string destinationPath)
        => Notify(ScheduleStorePersistenceBoundary.Published);

    public void OnCleanupPrepared(long generation, string sourcePath, string claimPath)
    {
    }

    public void OnCleanupClaimed(long generation, string claimPath)
    {
    }

    public void OnCleanupDeleting(long generation, string claimPath)
    {
    }

    private void Notify(ScheduleStorePersistenceBoundary boundary)
    {
        try
        {
            _observer?.Invoke(boundary);
        }
        catch (Exception exception) when (exception is not ScheduleStoreBoundaryObserverException)
        {
            throw new ScheduleStoreBoundaryObserverException(exception);
        }
    }
}
