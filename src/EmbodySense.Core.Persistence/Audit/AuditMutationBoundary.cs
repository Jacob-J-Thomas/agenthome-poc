using System.Collections.Concurrent;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Audit;

/// <summary>Owns the canonical bounded mutation boundary for one workspace audit ledger.</summary>
/// <remarks>
/// Lock order is process-local path semaphore, cross-process sidecar lease, and then the audit-ledger stream. Callers must
/// dispose ledger streams before this boundary so ownership is released in the reverse order. The retained no-follow path
/// session validates sidecar and ledger bindings at commit checkpoints on Windows, macOS, and Linux. The OS releases the
/// sidecar lease if its owner exits without disposing it.
/// </remarks>
internal sealed class AuditMutationBoundary : IAsyncDisposable
{
    internal const string LockFileName = ".events.ndjson.mutation.lock";
    private static readonly TimeSpan _acquisitionTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _initializationRetryDelay = TimeSpan.FromMilliseconds(20);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _processLocks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private CapabilityCatalogPathSession? _pathSession;
    private SemaphoreSlim? _processLock;

    private AuditMutationBoundary(CapabilityCatalogPathSession pathSession, SemaphoreSlim processLock)
    {
        _pathSession = pathSession;
        _processLock = processLock;
    }

    /// <summary>Acquires bounded process-local and cross-process ownership for the workspace audit ledger.</summary>
    /// <param name="paths">The canonical workspace paths.</param>
    /// <param name="cancellationToken">The token used while waiting for either ownership layer.</param>
    /// <returns>The retained mutation boundary.</returns>
    /// <exception cref="OperationCanceledException">The wait was canceled.</exception>
    /// <exception cref="TimeoutException">Mutation ownership remained unavailable for the complete bounded wait.</exception>
    /// <exception cref="IOException">Safe path or sidecar ownership could not be established.</exception>
    internal static async Task<AuditMutationBoundary> AcquireAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var deadline = new CancellationTokenSource(_acquisitionTimeout);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await AcquireWithinDeadlineAsync(paths, acquisition.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The audit mutation boundary remained owned for the complete bounded wait.", exception);
        }
    }

    private static async Task<AuditMutationBoundary> AcquireWithinDeadlineAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        var processLock = _processLocks.GetOrAdd(paths.EventsLogPath, _ => new SemaphoreSlim(1, 1));
        await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        CapabilityCatalogPathSession? pathSession = null;
        var releaseProcessLock = true;
        try
        {
            pathSession = await OpenPathSessionAsync(paths.AuditPath, cancellationToken).ConfigureAwait(false);
            var lockPath = Path.Combine(paths.AuditPath, LockFileName);
            if (!await pathSession.TryAcquireLockAsync(
                lockPath,
                createParent: false,
                cancellationToken,
                throwOnContentionTimeout: false,
                retryInitializationRaces: true).ConfigureAwait(false))
            {
                throw new TimeoutException("The audit mutation boundary remained owned by another process for the complete bounded wait.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var boundary = new AuditMutationBoundary(pathSession, processLock);
            pathSession = null;
            releaseProcessLock = false;
            return boundary;
        }
        finally
        {
            pathSession?.Dispose();
            if (releaseProcessLock)
            {
                processLock.Release();
            }
        }
    }

    private static async Task<CapabilityCatalogPathSession> OpenPathSessionAsync(string auditPath, CancellationToken cancellationToken)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return CapabilityCatalogPathSession.Open(auditPath, comparison, createRoot: true)
                    ?? throw new IOException("The audit mutation boundary could not open its canonical audit directory.");
            }
            catch (Exception exception) when (attempt < 3 && IsWindowsRootInitializationRace(exception))
            {
                await Task.Delay(_initializationRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsWindowsRootInitializationRace(Exception exception)
        => OperatingSystem.IsWindows()
            && exception is IOException or UnauthorizedAccessException
            && (exception.HResult & 0xFFFF) is 2 or 3 or 5 or 80 or 183;

    /// <summary>Opens the canonical ledger through retained no-follow directory authority.</summary>
    internal FileStream OpenLedgerStream()
    {
        var pathSession = _pathSession ?? throw new ObjectDisposedException(nameof(AuditMutationBoundary));
        return pathSession.OpenBoundUpdateLease(Path.Combine(pathSession.Root, "events.ndjson"));
    }

    /// <summary>Proves that the retained ledger and sidecar remain bound to their canonical paths.</summary>
    internal void RequireLedgerBinding(FileStream stream)
    {
        var pathSession = _pathSession ?? throw new ObjectDisposedException(nameof(AuditMutationBoundary));
        pathSession.EnsureBoundUpdateLease(Path.Combine(pathSession.Root, "events.ndjson"), stream);
    }

    /// <summary>Releases cross-process ownership before process-local ownership.</summary>
    public ValueTask DisposeAsync()
    {
        var pathSession = Interlocked.Exchange(ref _pathSession, null);
        var processLock = Interlocked.Exchange(ref _processLock, null);
        try
        {
            pathSession?.Dispose();
        }
        finally
        {
            processLock?.Release();
        }

        return ValueTask.CompletedTask;
    }
}
