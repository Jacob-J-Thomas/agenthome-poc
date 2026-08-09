using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Startup.Capabilities;

namespace EmbodySense.Core.Startup.Tests.Capabilities;

internal sealed class InterleavingBuiltInCapabilityCatalogSeedObserver(string afterOperationId, Func<CapabilityCatalogEntry, CancellationToken, Task> interleave) : IBuiltInCapabilityCatalogSeedObserver
{
    private int _interleavingCount;

    public int InterleavingCount => _interleavingCount;

    public async Task TransitionCommittedAsync(CapabilityCatalogEntry entry, CancellationToken cancellationToken = default)
    {
        if (string.Equals(entry.LastOperationId, afterOperationId, StringComparison.Ordinal) && Interlocked.CompareExchange(ref _interleavingCount, 1, 0) == 0)
        {
            await interleave(entry, cancellationToken);
        }
    }
}
