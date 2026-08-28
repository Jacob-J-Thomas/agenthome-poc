using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Represents a workspace host.
/// </summary>
internal sealed class WorkspaceHost : IDisposable
{
    private readonly string _workspaceKey;
    private readonly Action<string, WorkspaceHost> _retireAfterBrokerFault;
    private int _disposed;
    private int _faulted;
    private int _activeLeaseCount;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkspaceHost"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="workspaceKey">The workspace key.</param>
    /// <param name="ownership">The ownership.</param>
    /// <param name="retireAfterBrokerFault">The callback that retires this host after a terminal broker fault.</param>
    /// <param name="brokerLifecycleObserver">The optional in-process observer for bounded broker lifecycle transitions.</param>
    public WorkspaceHost(WorkspacePaths paths, string workspaceKey, FileStream ownership, Action<string, WorkspaceHost> retireAfterBrokerFault, ICustomLoopCancellationBrokerLifecycleObserver? brokerLifecycleObserver)
    {
        _workspaceKey = workspaceKey;
        _retireAfterBrokerFault = retireAfterBrokerFault;
        Ownership = ownership;
        CancellationHost = new CustomLoopAttemptCancellationHost(paths, workspaceKey, HandleBrokerFault, brokerLifecycleObserver);
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
    /// Gets whether the broker entered terminal fault retirement.
    /// </summary>
    /// <value><see langword="true"/> after a terminal broker fault until the host is retired.</value>
    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>
    /// Gets whether an execution or busy-outcome lease still holds the faulted ownership boundary.
    /// </summary>
    /// <value><see langword="true"/> while an active lease must finish before ownership is released.</value>
    public bool HasActiveLeases => Volatile.Read(ref _activeLeaseCount) != 0;

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
    /// Adds one active execution or busy-outcome lease that prevents fault retirement from releasing ownership.
    /// </summary>
    public void AddActiveLease() => Interlocked.Increment(ref _activeLeaseCount);

    /// <summary>
    /// Removes one active execution or busy-outcome lease and reports whether terminal fault retirement can release ownership.
    /// </summary>
    /// <returns><see langword="true"/> when the faulted host has no remaining active leases.</returns>
    public bool ReleaseActiveLease()
    {
        var remaining = Interlocked.Decrement(ref _activeLeaseCount);
        if (remaining < 0)
        {
            throw new InvalidOperationException("The workspace host active lease count cannot become negative.");
        }

        return IsFaulted && remaining == 0;
    }

    /// <summary>
    /// Marks the host as faulted and reports whether no active lease remains to preserve ownership.
    /// </summary>
    /// <returns><see langword="true"/> when the host can be retired immediately.</returns>
    public bool MarkFaulted() => Interlocked.Exchange(ref _faulted, 1) == 0 && !HasActiveLeases;

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

    private void HandleBrokerFault()
    {
        _ = MarkFaulted();
        _retireAfterBrokerFault(_workspaceKey, this);
    }
}
