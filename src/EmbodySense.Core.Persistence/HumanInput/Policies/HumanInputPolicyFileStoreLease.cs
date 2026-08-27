using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.HumanInput.Policies;

internal sealed class HumanInputPolicyFileStoreLease : IAsyncDisposable
{
    private readonly CapabilityCatalogPathSession _session;
    private int _disposed;

    internal HumanInputPolicyFileStoreLease(CapabilityCatalogPathSession session)
    {
        _session = session;
    }

    internal CapabilityCatalogPathSession Session => _session;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
