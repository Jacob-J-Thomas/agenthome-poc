namespace EmbodySense.Core.Persistence.Loops;

internal sealed class MutationLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _processGate;

    public MutationLease(FileStream stream, SemaphoreSlim processGate)
    {
        _stream = stream;
        _processGate = processGate;
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
        _processGate.Release();
    }
}
