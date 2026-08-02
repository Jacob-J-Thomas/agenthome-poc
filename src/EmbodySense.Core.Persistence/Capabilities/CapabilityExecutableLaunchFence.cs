using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class CapabilityExecutableLaunchFence(ICapabilityAuthorityLease authorityLease) : ICapabilityExecutableLaunchFence
{
    private ICapabilityAuthorityLease? _authorityLease = authorityLease;

    public async ValueTask DisposeAsync()
    {
        var lease = Interlocked.Exchange(ref _authorityLease, null);
        if (lease is not null)
        {
            await lease.DisposeAsync();
        }
    }
}
