using System.Collections.Concurrent;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Capabilities;

/// <summary>Provides one reentrant cross-process capability-authority transaction fence per physical workspace.</summary>
/// <remarks>Nested operations execute reentrantly only while an outer callback is active. Retained validated leases cannot be acquired from a nested transaction.</remarks>
public sealed class CapabilityAuthorityTransaction : ICapabilityAuthorityTransaction
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processGates = new(StringComparer.Ordinal);
    private static readonly AsyncLocal<CapabilityAuthorityAmbientFrame?> _ambientTransaction = new();
    private readonly string _identity;
    private readonly SemaphoreSlim _processGate;
    private readonly ICapabilityAuthorityLockSessionProvider _lockSessionProvider;

    /// <summary>Creates the shared transaction boundary for one workspace.</summary>
    /// <param name="paths">The initialized workspace paths.</param>
    /// <param name="durabilityBarrier">The optional trusted filesystem durability adapter.</param>
    /// <param name="lockSessionProvider">The optional trusted authority-lock adapter.</param>
    /// <param name="timeProvider">The optional trusted clock used by bounded lock-retry delays.</param>
    public CapabilityAuthorityTransaction(WorkspacePaths paths, ICapabilityCatalogDurabilityBarrier? durabilityBarrier = null, ICapabilityAuthorityLockSessionProvider? lockSessionProvider = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var lockPath = Path.GetFullPath(paths.CapabilityAuthorityLockPath);
        _identity = OperatingSystem.IsWindows() ? lockPath.ToUpperInvariant() : lockPath;
        _processGate = _processGates.GetOrAdd(_identity, _ => new SemaphoreSlim(1, 1));
        _lockSessionProvider = lockSessionProvider ?? new CapabilityAuthorityLockSessionProvider(paths.RootPath, lockPath, durabilityBarrier ?? NativeCapabilityCatalogDurabilityBarrier.Instance, timeProvider);
    }

    /// <inheritdoc />
    public async Task<TResult> ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        if (_ambientTransaction.Value?.ContainsActive(_identity) == true)
        {
            return await operation(cancellationToken);
        }

        await _processGate.WaitAsync(cancellationToken);
        var ownsGate = true;
        IAsyncDisposable? session = null;
        var previous = _ambientTransaction.Value;
        CapabilityAuthorityAmbientFrame? frame = null;
        try
        {
            session = await _lockSessionProvider.TryAcquireAsync(cancellationToken) ?? throw new IOException("The workspace capability-authority boundary is unavailable.");
            frame = new CapabilityAuthorityAmbientFrame(_identity, previous);
            _ambientTransaction.Value = frame;
            return await operation(cancellationToken);
        }
        finally
        {
            frame?.Invalidate();
            _ambientTransaction.Value = previous;
            try
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
            }
            finally
            {
                if (ownsGate)
                {
                    _processGate.Release();
                    ownsGate = false;
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task<ICapabilityAuthorityLease?> AcquireValidatedLeaseAsync(Func<CancellationToken, Task<bool>> validator, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(validator);
        cancellationToken.ThrowIfCancellationRequested();
        if (_ambientTransaction.Value?.ContainsActive(_identity) == true)
        {
            throw new InvalidOperationException("A retained capability-authority lease cannot escape a nested transaction.");
        }

        await _processGate.WaitAsync(cancellationToken);
        var ownsGate = true;
        IAsyncDisposable? session = null;
        var previous = _ambientTransaction.Value;
        CapabilityAuthorityAmbientFrame? frame = null;
        try
        {
            session = await _lockSessionProvider.TryAcquireAsync(cancellationToken) ?? throw new IOException("The workspace capability-authority boundary is unavailable.");
            frame = new CapabilityAuthorityAmbientFrame(_identity, previous);
            _ambientTransaction.Value = frame;
            if (!await validator(cancellationToken))
            {
                return null;
            }

            var lease = new CapabilityAuthorityLease(session, _processGate);
            session = null;
            ownsGate = false;
            return lease;
        }
        finally
        {
            frame?.Invalidate();
            _ambientTransaction.Value = previous;
            try
            {
                if (session is not null)
                {
                    await session.DisposeAsync();
                }
            }
            finally
            {
                if (ownsGate)
                {
                    _processGate.Release();
                    ownsGate = false;
                }
            }
        }
    }

}
