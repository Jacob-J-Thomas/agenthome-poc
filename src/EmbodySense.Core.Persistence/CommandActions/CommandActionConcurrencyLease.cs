namespace EmbodySense.Core.Persistence.CommandActions;

internal sealed class CommandActionConcurrencyLease : IAsyncDisposable
{
    private FileStream? _stream;

    internal CommandActionConcurrencyLease(FileStream stream)
    {
        _stream = stream;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
