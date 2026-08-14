namespace EmbodySense.Core.Persistence.Capabilities;

internal sealed class CapabilityAuthorityLockSessionProvider : ICapabilityAuthorityLockSessionProvider
{
    private readonly CapabilityCatalogPathGuard _guard;
    private readonly string _lockPath;

    internal CapabilityAuthorityLockSessionProvider(string rootPath, string lockPath, ICapabilityCatalogDurabilityBarrier durabilityBarrier, TimeProvider? timeProvider = null)
    {
        _guard = new CapabilityCatalogPathGuard(rootPath, durabilityBarrier, timeProvider: timeProvider);
        _lockPath = lockPath;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(CancellationToken cancellationToken = default) => await _guard.TryAcquireExclusiveSessionAsync(_lockPath, createRoot: false, cancellationToken);
}
