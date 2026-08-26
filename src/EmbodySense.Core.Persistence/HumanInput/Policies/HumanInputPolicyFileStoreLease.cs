using System.Threading;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

internal sealed class HumanInputPolicyFileStoreLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _gate;
    private int _disposed;

    internal HumanInputPolicyFileStoreLease(FileStream stream, SemaphoreSlim gate)
    {
        _stream = stream;
        _gate = gate;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stream.DisposeAsync().ConfigureAwait(false);
        _gate.Release();
    }
}
