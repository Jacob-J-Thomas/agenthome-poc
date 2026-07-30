namespace EmbodySense.Core.Application.Runtime.State;

/// <summary>
/// Represents an exclusive access lease.
/// </summary>
internal sealed class ExclusiveAccessLease : IDisposable
{
    private SemaphoreSlim? _gate;
    private IDisposable? _workspaceLease;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExclusiveAccessLease"/> type.
    /// </summary>
    /// <param name="gate">The gate.</param>
    /// <param name="workspaceLease">The workspace lease.</param>
    public ExclusiveAccessLease(SemaphoreSlim gate, IDisposable? workspaceLease)
    {
        _gate = gate;
        _workspaceLease = workspaceLease;
    }

    /// <summary>
    /// Executes the dispose operation.
    /// </summary>
    /// <returns>The operation.</returns>
    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _workspaceLease, null)?.Dispose();
        }
        finally
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
