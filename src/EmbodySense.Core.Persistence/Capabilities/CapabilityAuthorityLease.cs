using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class CapabilityAuthorityLease(IAsyncDisposable session, SemaphoreSlim processGate) : ICapabilityAuthorityLease
{
    private IAsyncDisposable? _session = session;
    private SemaphoreSlim? _processGate = processGate;

    public async ValueTask DisposeAsync()
    {
        var gate = Interlocked.Exchange(ref _processGate, null);
        if (gate is null)
        {
            return;
        }

        var session = Interlocked.Exchange(ref _session, null);
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
