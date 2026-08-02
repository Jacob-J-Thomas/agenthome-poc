namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Provides the production no-op queue durability observer.</summary>
internal sealed class NullTriggerQueueDurabilityObserver : ITriggerQueueDurabilityObserver
{
    /// <summary>Gets the shared no-op instance.</summary>
    public static NullTriggerQueueDurabilityObserver Instance { get; } = new();

    private NullTriggerQueueDurabilityObserver()
    {
    }

    /// <inheritdoc />
    public void OnStaged(long generation, string stagingPath, string destinationPath)
    {
    }

    /// <inheritdoc />
    public void OnPublishing(long generation, string stagingPath, string destinationPath)
    {
    }

    /// <inheritdoc />
    public void OnPublishingDirectoryBound(long generation, string stagingPath, string destinationPath)
    {
    }

    /// <inheritdoc />
    public void OnPublished(long generation, string destinationPath)
    {
    }

    /// <inheritdoc />
    public void OnCleanupPrepared(long generation, string sourcePath, string claimPath)
    {
    }

    /// <inheritdoc />
    public void OnCleanupClaimed(long generation, string claimPath)
    {
    }

    /// <inheritdoc />
    public void OnCleanupDeleting(long generation, string claimPath)
    {
    }
}
