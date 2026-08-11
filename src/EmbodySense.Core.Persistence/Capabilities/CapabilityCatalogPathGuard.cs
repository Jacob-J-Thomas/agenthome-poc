namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Creates stable, contained, no-follow filesystem sessions for capability catalog persistence.</summary>
internal sealed class CapabilityCatalogPathGuard
{
    private readonly string _root;
    private readonly StringComparison _comparison;
    private readonly ICapabilityCatalogDurabilityBarrier _durabilityBarrier;
    private readonly ICapabilityCatalogPathObserver? _pathObserver;

    public CapabilityCatalogPathGuard(string root, ICapabilityCatalogDurabilityBarrier durabilityBarrier, ICapabilityCatalogPathObserver? pathObserver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(durabilityBarrier);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _durabilityBarrier = durabilityBarrier;
        _pathObserver = pathObserver;
    }

    public async Task<CapabilityCatalogPathSession?> TryAcquireExclusiveSessionAsync(string lockPath, bool createRoot, CancellationToken cancellationToken, bool createLockParent = true)
    {
        var session = CapabilityCatalogPathSession.Open(_root, _comparison, createRoot, _durabilityBarrier, _pathObserver);
        if (session is null)
        {
            return null;
        }

        try
        {
            if (!await session.TryAcquireLockAsync(lockPath, createLockParent, cancellationToken))
            {
                await session.DisposeAsync();
                return null;
            }
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }
}
