using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class DelegateCapabilityAuthorityLockSessionProvider(Func<int, CancellationToken, Task<IAsyncDisposable?>> acquire) : ICapabilityAuthorityLockSessionProvider
{
    private int _attempts;

    public Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default) => acquire(Interlocked.Increment(ref _attempts), cancellationToken);
}
