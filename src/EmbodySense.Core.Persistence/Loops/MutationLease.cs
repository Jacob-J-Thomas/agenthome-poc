namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Couples a cross-process mutation file lease with its process-local semaphore ownership.
/// </summary>
internal sealed class MutationLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _processGate;

    /// <summary>
    /// Initializes a new instance of the <see cref="MutationLease"/> type.
    /// </summary>
    /// <param name="stream">The stream.</param>
    /// <param name="processGate">The process gate.</param>
    public MutationLease(FileStream stream, SemaphoreSlim processGate)
    {
        _stream = stream;
        _processGate = processGate;
    }

    /// <summary>
    /// Closes the file lease before releasing the process-local mutation gate.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _processGate.Release();
    }
}
