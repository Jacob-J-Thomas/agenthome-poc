namespace EmbodySense.Core.Persistence.Triggers;

using EmbodySense.Core.Persistence.Triggers.Models;

/// <summary>Owns both the in-process queue mutex and cross-process file lock for one mutation.</summary>
internal sealed class TriggerQueueMutationLease : IDisposable
{
    private FileStream? _stream;
    private SemaphoreSlim? _semaphore;

    /// <summary>Initializes an owned mutation lease.</summary>
    public TriggerQueueMutationLease(FileStream stream, SemaphoreSlim semaphore, IReadOnlyList<TriggerQueueDirectorySnapshot> rootSnapshot, string lockPath, TriggerQueueFileIdentity lockIdentity)
    {
        _stream = stream;
        _semaphore = semaphore;
        RootSnapshot = rootSnapshot;
        LockPath = lockPath;
        LockIdentity = lockIdentity;
    }

    /// <summary>Gets the exact governed directory chain captured for this lease.</summary>
    public IReadOnlyList<TriggerQueueDirectorySnapshot> RootSnapshot { get; }

    /// <summary>Gets the canonical pathname of the exact locked file.</summary>
    public string LockPath { get; }

    /// <summary>Gets the exact locked file identity observed through both path and handle at acquisition.</summary>
    public TriggerQueueFileIdentity LockIdentity { get; }

    /// <summary>Releases both lock layers exactly once.</summary>
    public void Dispose()
    {
        var stream = Interlocked.Exchange(ref _stream, null);
        var semaphore = Interlocked.Exchange(ref _semaphore, null);
        stream?.Dispose();
        semaphore?.Release();
    }
}
