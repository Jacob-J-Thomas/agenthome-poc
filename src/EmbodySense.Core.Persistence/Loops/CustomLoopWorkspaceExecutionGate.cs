using EmbodySense.Core.Common.Loops.Custom;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Coordinates one local custom-loop execution owner with the cross-process workspace-host lease.
/// </summary>
/// <remarks>
/// Host ownership is durable only for the lifetime of the lock stream. Active-operation and busy-outcome reservations are
/// in-memory concurrency state and are not durable receipts; the invocation-operation store establishes receipt durability.
/// Losing or failing to acquire the host lease returns <c>WorkspaceHostUnavailable</c> and does not reserve a busy outcome.
/// </remarks>
public sealed class CustomLoopWorkspaceExecutionGate : ICustomLoopWorkspaceExecutionGate, ICustomLoopAttemptCancellationBroker
{
    private static readonly object _hostsSync = new();
    private static readonly Dictionary<string, WorkspaceHost> _hosts = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly WorkspacePaths _paths;
    private readonly string _workspaceKey;
    private WorkspaceHost? _host;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="CustomLoopWorkspaceExecutionGate"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    public CustomLoopWorkspaceExecutionGate(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _workspaceKey = CanonicalWorkspaceKey(paths.RootPath);

        lock (_hostsSync)
        {
            _host = TryAttachOrAcquireHost();
        }
    }

    /// <summary>
    /// Gets whether this process owns or can attach to the workspace's cross-process host lease.
    /// </summary>
    /// <value><see langword="true"/> when local custom-loop hosting is available.</value>
    public bool IsWorkspaceHostAvailable
    {
        get
        {
            lock (_hostsSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return (_host ??= TryAttachOrAcquireHost()) is not null;
            }
        }
    }

    /// <summary>
    /// Attempts to acquire the single in-memory execution slot for a canonical authorized request.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="requestHash">The request hash.</param>
    /// <returns>The custom loop execution lease result.</returns>
    public CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash)
    {
        ValidateRequest(operationId, requestHash);

        lock (_hostsSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var host = _host ??= TryAttachOrAcquireHost();
            if (host is null)
            {
                return new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, null, "custom_workspace_host_busy: another process owns custom-loop hosting for this workspace.");
            }

            if (host.BusyOutcomeReservations.TryGetValue(operationId, out var busyReservation))
            {
                return SameRequest(busyReservation.RequestHash, requestHash)
                    ? new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.OperationInProgress, null, "The invocation operation is durably recording a workspace-busy outcome.")
                    : new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.OperationConflict, null, "The invocation operation id is reserved for different canonical authorized request content.");
            }

            if (host.ActiveOperationId is null)
            {
                host.ActiveOperationId = operationId;
                host.ActiveRequestHash = requestHash;
                host.Generation++;
                host.ReferenceCount++;
                var lease = new ExecutionLease(_workspaceKey, host, operationId, host.Generation);
                return new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.Acquired, lease, "Custom-loop execution ownership was acquired without waiting.");
            }

            if (!string.Equals(host.ActiveOperationId, operationId, StringComparison.Ordinal))
            {
                return new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.WorkspaceBusy, null, "Another custom-loop run is actively executing in this workspace; no request was queued.");
            }

            var status = string.Equals(host.ActiveRequestHash, requestHash, StringComparison.Ordinal)
                ? CustomLoopExecutionLeaseStatus.OperationInProgress
                : CustomLoopExecutionLeaseStatus.OperationConflict;
            var detail = status == CustomLoopExecutionLeaseStatus.OperationInProgress
                ? "The same custom-loop operation is already executing; retry its durable receipt later."
                : "The active operation id is bound to different canonical authorized request content.";
            return new CustomLoopExecutionLeaseResult(status, null, detail);
        }
    }

    /// <summary>
    /// Reserves an in-memory busy-outcome slot while another operation owns workspace execution.
    /// </summary>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="requestHash">The request hash.</param>
    /// <returns>The custom loop execution lease result.</returns>
    public CustomLoopExecutionLeaseResult TryReserveWorkspaceBusyOutcome(string operationId, string requestHash)
    {
        ValidateRequest(operationId, requestHash);

        lock (_hostsSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var host = _host ??= TryAttachOrAcquireHost();
            if (host is null)
            {
                return new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.WorkspaceHostUnavailable, null, "custom_workspace_host_busy: another process owns custom-loop hosting for this workspace.");
            }

            if (host.BusyOutcomeReservations.TryGetValue(operationId, out var existingReservation))
            {
                return SameRequest(existingReservation.RequestHash, requestHash)
                    ? new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.OperationInProgress, null, "The invocation operation is already recording a workspace-busy outcome.")
                    : new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.OperationConflict, null, "The invocation operation id is reserved for different canonical authorized request content.");
            }

            if (host.ActiveOperationId is null)
            {
                return new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.WorkspaceAvailable, null, "Workspace execution ownership became available before the busy outcome was reserved.");
            }

            if (string.Equals(host.ActiveOperationId, operationId, StringComparison.Ordinal))
            {
                var status = SameRequest(host.ActiveRequestHash, requestHash)
                    ? CustomLoopExecutionLeaseStatus.OperationInProgress
                    : CustomLoopExecutionLeaseStatus.OperationConflict;
                var detail = status == CustomLoopExecutionLeaseStatus.OperationInProgress
                    ? "The same custom-loop operation acquired execution ownership before a busy outcome could be reserved."
                    : "The active operation id is bound to different canonical authorized request content.";
                return new CustomLoopExecutionLeaseResult(status, null, detail);
            }

            host.BusyOutcomeGeneration++;
            host.ReferenceCount++;
            var reservation = new BusyOutcomeReservation(requestHash, host.BusyOutcomeGeneration);
            host.BusyOutcomeReservations.Add(operationId, reservation);
            var lease = new BusyOutcomeReservationLease(_workspaceKey, host, operationId, reservation.Generation);
            return new CustomLoopExecutionLeaseResult(CustomLoopExecutionLeaseStatus.BusyOutcomeReserved, lease, "The workspace-busy outcome reservation prevents the same operation from acquiring execution ownership until its receipt is finalized.");
        }
    }

    /// <summary>
    /// Registers the provider cancellation source for the workspace's active run attempt.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="cancellation">The source signaled by a cancellation request.</param>
    /// <param name="competingCancellationToken">A separate host or caller cancellation that must not be misreported as provider interruption.</param>
    /// <returns>The custom loop attempt cancellation registration.</returns>
    public ICustomLoopAttemptCancellationRegistration RegisterActiveAttempt(string runId, CancellationTokenSource cancellation, CancellationToken competingCancellationToken = default)
    {
        CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(cancellation);
        lock (_hostsSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var host = _host ??= TryAttachOrAcquireHost();
            if (host is null)
            {
                throw new InvalidOperationException("The active provider attempt cannot register because this process does not own custom-loop hosting.");
            }

            return host.CancellationHost.RegisterActiveAttempt(runId, cancellation, competingCancellationToken);
        }
    }

    /// <summary>
    /// Routes a cancellation request to the local host or its authenticated cross-process cancellation channel.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the custom loop attempt cancellation result.</returns>
    public async Task<CustomLoopAttemptCancellationResult> RequestCancellationAsync(string runId, string operationId, CancellationToken cancellationToken = default)
    {
        CustomLoopArtifactIdentifier.Require(runId, nameof(runId));
        CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        WorkspaceHost? localHost;
        lock (_hostsSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            localHost = _host;
        }

        if (localHost is not null)
        {
            return await localHost.CancellationHost.RequestCancellationAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return await CustomLoopAttemptCancellationHost.RequestRemoteCancellationAsync(_paths, runId, operationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases this gate's host reference and any process-wide host lease it solely owns.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_hostsSync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            if (_host is not null)
            {
                ReleaseReference(_workspaceKey, _host);
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Explicitly releases this instance's workspace-host reference without disposing the gate.
    /// </summary>
    public void RelinquishWorkspaceHost()
    {
        lock (_hostsSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host is null)
            {
                return;
            }

            var host = _host;
            _host = null;
            ReleaseReference(_workspaceKey, host);
        }
    }

    /// <summary>
    /// Executes the release lease operation.
    /// </summary>
    /// <param name="workspaceKey">The workspace key.</param>
    /// <param name="host">The host.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="generation">The generation.</param>
    /// <returns>The operation.</returns>
    internal static void ReleaseLease(string workspaceKey, WorkspaceHost host, string operationId, long generation)
    {
        lock (_hostsSync)
        {
            if (host.Generation == generation && string.Equals(host.ActiveOperationId, operationId, StringComparison.Ordinal))
            {
                host.ActiveOperationId = null;
                host.ActiveRequestHash = null;
            }

            ReleaseReference(workspaceKey, host);
        }
    }

    /// <summary>
    /// Executes the release busy outcome reservation operation.
    /// </summary>
    /// <param name="workspaceKey">The workspace key.</param>
    /// <param name="host">The host.</param>
    /// <param name="operationId">The operation ID.</param>
    /// <param name="generation">The generation.</param>
    /// <returns>The operation.</returns>
    internal static void ReleaseBusyOutcomeReservation(string workspaceKey, WorkspaceHost host, string operationId, long generation)
    {
        lock (_hostsSync)
        {
            if (host.BusyOutcomeReservations.TryGetValue(operationId, out var reservation) && reservation.Generation == generation)
            {
                host.BusyOutcomeReservations.Remove(operationId);
            }

            ReleaseReference(workspaceKey, host);
        }
    }

    private static void ReleaseReference(string workspaceKey, WorkspaceHost host)
    {
        host.ReferenceCount--;
        if (host.ReferenceCount != 0)
        {
            return;
        }

        _hosts.Remove(workspaceKey);
        host.Dispose();
    }

    private WorkspaceHost? TryAttachOrAcquireHost()
    {
        if (_hosts.TryGetValue(_workspaceKey, out var existing))
        {
            existing.ReferenceCount++;
            return existing;
        }

        var pathGuard = new CustomLoopArtifactPathGuard(_paths.RootPath);
        pathGuard.PrepareRoot(_paths.LoopRunsPath);
        FileStream? ownership = null;
        try
        {
            var hostLockPath = pathGuard.GetFilePath(_paths.LoopRunsPath, Path.GetFileName(_paths.CustomLoopHostLockPath));
            ownership = new FileStream(hostLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.WriteThrough);
            AcquireHostFileLock(ownership);
            pathGuard.GetFilePath(_paths.LoopRunsPath, Path.GetFileName(_paths.CustomLoopHostLockPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ownership?.Dispose();
            return null;
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }

        WorkspaceHost host;
        try
        {
            host = new WorkspaceHost(_paths, _workspaceKey, ownership);
        }
        catch
        {
            ownership.Dispose();
            throw;
        }

        _hosts.Add(_workspaceKey, host);
        return host;
    }

    private static string CanonicalWorkspaceKey(string rootPath)
    {
        var fullPath = Path.GetFullPath(rootPath);
        var pathRoot = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, pathRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void AcquireHostFileLock(FileStream ownership)
    {
        if (!CustomLoopCrossProcessFileLock.TryAcquire(ownership))
        {
            throw new IOException("Another process owns the custom-loop workspace host lock.");
        }
    }

    private static bool IsHash(string value)
    {
        return value.Length == CustomLoopLimits.Sha256HexCharacters && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool SameRequest(string? expectedHash, string requestHash) => string.Equals(expectedHash, requestHash, StringComparison.Ordinal);

    private static void ValidateRequest(string operationId, string requestHash)
    {
        CustomLoopArtifactIdentifier.Require(operationId, nameof(operationId), CustomLoopLimits.MaxMutationOperationIdCharacters);
        if (!IsHash(requestHash))
        {
            throw new ArgumentException("Request hash must be lowercase SHA-256 hexadecimal.", nameof(requestHash));
        }
    }

}
