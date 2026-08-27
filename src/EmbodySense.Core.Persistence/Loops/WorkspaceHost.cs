using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Represents a workspace host.
/// </summary>
internal sealed class WorkspaceHost : IDisposable
{
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceHost"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="workspaceKey">The workspace key.</param>
    /// <param name="ownership">The ownership.</param>
    /// <param name="retireAfterBrokerFault">The callback that retires this host after a terminal broker fault.</param>
    public WorkspaceHost(WorkspacePaths paths, string workspaceKey, FileStream ownership, Action<string, WorkspaceHost> retireAfterBrokerFault)
    {
        Ownership = ownership;
        CancellationHost = new CustomLoopAttemptCancellationHost(paths, workspaceKey);
        CancellationHost.BrokerFaulted += () => retireAfterBrokerFault(workspaceKey, this);
    }

    /// <summary>
    /// Gets the ownership file stream.
    /// </summary>
    /// <value>The ownership file stream.</value>
    public FileStream Ownership { get; }

    /// <summary>
    /// Gets the custom loop attempt cancellation host.
    /// </summary>
    /// <value>The custom loop attempt cancellation host.</value>
    public CustomLoopAttemptCancellationHost CancellationHost { get; }

    /// <summary>
    /// Gets whether this workspace host can still serve cancellation requests.
    /// </summary>
    /// <value><see langword="true"/> while its cancellation host has not faulted or stopped.</value>
    public bool IsAvailable => CancellationHost.IsAvailable;

    /// <summary>
    /// Gets the reference count.
    /// </summary>
    /// <value>The reference count.</value>
    public int ReferenceCount { get; set; } = 1;

    /// <summary>
    /// Gets the active operation ID.
    /// </summary>
    /// <value>The active operation ID.</value>
    public string? ActiveOperationId { get; set; }

    /// <summary>
    /// Gets the active request hash.
    /// </summary>
    /// <value>The active request hash.</value>
    public string? ActiveRequestHash { get; set; }

    /// <summary>
    /// Gets the generation.
    /// </summary>
    /// <value>The generation.</value>
    public long Generation { get; set; }

    /// <summary>
    /// Gets the busy outcome generation.
    /// </summary>
    /// <value>The busy outcome generation.</value>
    public long BusyOutcomeGeneration { get; set; }

    /// <summary>
    /// Gets the busy outcome reservations dictionary.
    /// </summary>
    /// <value>The busy outcome reservations dictionary.</value>
    public Dictionary<string, BusyOutcomeReservation> BusyOutcomeReservations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets whether this host has been retired and its cross-process ownership released.
    /// </summary>
    /// <value><see langword="true"/> after the host is retired or disposed.</value>
    public bool IsRetired => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Executes the dispose operation.
    /// </summary>
    /// <returns>The operation.</returns>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationHost.Dispose();
        Ownership.Dispose();
    }
}
