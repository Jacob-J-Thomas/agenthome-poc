using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class SerializingCapabilityAuthorityLease(SemaphoreSlim gate) : ICapabilityAuthorityLease
{
    private SemaphoreSlim? _gate = gate;

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
        return ValueTask.CompletedTask;
    }
}
