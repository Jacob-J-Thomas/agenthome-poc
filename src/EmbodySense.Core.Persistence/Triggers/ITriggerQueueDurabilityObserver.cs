namespace EmbodySense.Core.Persistence.Triggers;

/// <summary>Observes queue commit boundaries for crash testing and host diagnostics without changing queue content.</summary>
/// <remarks>Implementations must not mutate queue artifacts. An exception after publication means the caller must retry the same identity to learn the durable outcome.</remarks>
public interface ITriggerQueueDurabilityObserver
{
    /// <summary>Runs after exact native queue-directory authority is retained and before the lock file is opened or created.</summary>
    /// <param name="queueRoot">The canonical queue-root path bound to retained native authority.</param>
    void OnMutationDirectoryBound(string queueRoot);

    /// <summary>Runs after the bounded directory shape is observed and before artifact content is reopened and authenticated.</summary>
    /// <param name="queueRoot">The canonical queue-root path whose direct children were observed.</param>
    void OnArtifactsObserved(string queueRoot);

    /// <summary>Runs immediately before staging creation while exact queue-directory authority remains retained.</summary>
    /// <param name="generation">The candidate ledger generation.</param>
    /// <param name="precursorPath">The create-new precursor path.</param>
    /// <param name="destinationPath">The immutable no-replace publication path.</param>
    void OnStagingDirectoryBound(long generation, string precursorPath, string destinationPath);

    /// <summary>Runs after an empty create-new precursor is opened in the retained directory and before its identity is named.</summary>
    /// <param name="generation">The candidate ledger generation.</param>
    /// <param name="precursorPath">The exact empty precursor path.</param>
    /// <param name="destinationPath">The immutable no-replace publication path.</param>
    void OnStagingPrecursorCreated(long generation, string precursorPath, string destinationPath);

    /// <summary>Runs after a complete temporary ledger has been flushed but before atomic publication.</summary>
    /// <param name="generation">The candidate ledger generation.</param>
    /// <param name="stagingPath">The exact create-new staging path.</param>
    /// <param name="destinationPath">The immutable no-replace publication path.</param>
    void OnStaged(long generation, string stagingPath, string destinationPath);

    /// <summary>Runs immediately before the flushed staging file is published through an atomic no-replace rename.</summary>
    /// <param name="generation">The candidate ledger generation.</param>
    /// <param name="stagingPath">The exact identity-proven staging path.</param>
    /// <param name="destinationPath">The immutable no-replace publication path.</param>
    void OnPublishing(long generation, string stagingPath, string destinationPath);

    /// <summary>Runs after the authoritative queue directory has been pinned and immediately before native publication.</summary>
    /// <param name="generation">The candidate ledger generation.</param>
    /// <param name="stagingPath">The exact identity-proven staging path in the pinned directory.</param>
    /// <param name="destinationPath">The immutable no-replace publication path.</param>
    void OnPublishingDirectoryBound(long generation, string stagingPath, string destinationPath);

    /// <summary>Runs after the atomic ledger publication has succeeded.</summary>
    /// <param name="generation">The published ledger generation.</param>
    /// <param name="destinationPath">The immutable published path.</param>
    void OnPublished(long generation, string destinationPath);

    /// <summary>Runs after cleanup identity validation but before the artifact is atomically claimed under a unique path.</summary>
    /// <param name="generation">The artifact generation.</param>
    /// <param name="sourcePath">The exact path about to be claimed.</param>
    /// <param name="claimPath">The create-new claim path that will receive the artifact without replacement.</param>
    void OnCleanupPrepared(long generation, string sourcePath, string claimPath);

    /// <summary>Runs after a cleanup artifact has been durably claimed and its identity has been revalidated, but before exact reclamation.</summary>
    /// <param name="generation">The artifact generation.</param>
    /// <param name="claimPath">The exact identity-proven cleanup claim path.</param>
    void OnCleanupClaimed(long generation, string claimPath);

    /// <summary>Runs after final claim proof and immediately before handle-bound deletion or authenticated tombstoning.</summary>
    /// <param name="generation">The artifact generation.</param>
    /// <param name="claimPath">The exact identity-proven cleanup claim path.</param>
    void OnCleanupDeleting(long generation, string claimPath);
}
