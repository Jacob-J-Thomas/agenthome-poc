using EmbodySense.Core.Persistence.Triggers;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class TriggerQueueLockHoldingObserver(string readyMarker, string releaseMarker) : ITriggerQueueDurabilityObserver
{
    private int _hasHeldLock;

    public void OnMutationDirectoryBound(string queueRoot)
    {
    }

    public void OnArtifactsObserved(string queueRoot)
    {
        if (Interlocked.Exchange(ref _hasHeldLock, 1) == 0)
        {
            CrossProcessMarkerProtocol.SignalReadyAndWaitForRelease(readyMarker, releaseMarker);
        }
    }

    public void OnStagingDirectoryBound(long generation, string precursorPath, string destinationPath)
    {
    }

    public void OnStagingPrecursorCreated(long generation, string precursorPath, string destinationPath)
    {
    }

    public void OnStaged(long generation, string stagingPath, string destinationPath)
    {
    }

    public void OnPublishing(long generation, string stagingPath, string destinationPath)
    {
    }

    public void OnPublishingDirectoryBound(long generation, string stagingPath, string destinationPath)
    {
    }

    public void OnPublished(long generation, string destinationPath)
    {
    }

    public void OnCleanupPrepared(long generation, string sourcePath, string claimPath)
    {
    }

    public void OnCleanupClaimed(long generation, string claimPath)
    {
    }

    public void OnCleanupDeleting(long generation, string claimPath)
    {
    }
}
