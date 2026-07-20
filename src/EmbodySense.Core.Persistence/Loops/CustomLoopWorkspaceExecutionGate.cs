using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models.Custom;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class CustomLoopWorkspaceExecutionGate : ICustomLoopWorkspaceExecutionGate
{
    private static readonly object HostsSync = new();
    private static readonly Dictionary<string, WorkspaceHost> Hosts = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly WorkspacePaths _paths;
    private readonly string _workspaceKey;
    private WorkspaceHost? _host;
    private bool _disposed;

    public CustomLoopWorkspaceExecutionGate(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _workspaceKey = CanonicalWorkspaceKey(paths.RootPath);

        lock (HostsSync)
        {
            _host = TryAttachOrAcquireHost();
        }
    }

    public bool IsWorkspaceHostAvailable
    {
        get
        {
            lock (HostsSync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return (_host ??= TryAttachOrAcquireHost()) is not null;
            }
        }
    }

    public CustomLoopExecutionLeaseResult TryAcquire(string operationId, string requestHash)
    {
        ValidateRequest(operationId, requestHash);

        lock (HostsSync)
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

    public CustomLoopExecutionLeaseResult TryReserveWorkspaceBusyOutcome(string operationId, string requestHash)
    {
        ValidateRequest(operationId, requestHash);

        lock (HostsSync)
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

    public ValueTask DisposeAsync()
    {
        lock (HostsSync)
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

    private static void ReleaseLease(string workspaceKey, WorkspaceHost host, string operationId, long generation)
    {
        lock (HostsSync)
        {
            if (host.Generation == generation && string.Equals(host.ActiveOperationId, operationId, StringComparison.Ordinal))
            {
                host.ActiveOperationId = null;
                host.ActiveRequestHash = null;
            }

            ReleaseReference(workspaceKey, host);
        }
    }

    private static void ReleaseBusyOutcomeReservation(string workspaceKey, WorkspaceHost host, string operationId, long generation)
    {
        lock (HostsSync)
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

        Hosts.Remove(workspaceKey);
        host.Ownership.Dispose();
    }

    private WorkspaceHost? TryAttachOrAcquireHost()
    {
        if (Hosts.TryGetValue(_workspaceKey, out var existing))
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

        var host = new WorkspaceHost(ownership);
        Hosts.Add(_workspaceKey, host);
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

    private sealed class WorkspaceHost
    {
        public WorkspaceHost(FileStream ownership)
        {
            Ownership = ownership;
        }

        public FileStream Ownership { get; }

        public int ReferenceCount { get; set; } = 1;

        public string? ActiveOperationId { get; set; }

        public string? ActiveRequestHash { get; set; }

        public long Generation { get; set; }

        public long BusyOutcomeGeneration { get; set; }

        public Dictionary<string, BusyOutcomeReservation> BusyOutcomeReservations { get; } = new(StringComparer.Ordinal);
    }

    private sealed record BusyOutcomeReservation(string RequestHash, long Generation);

    private sealed class ExecutionLease : ICustomLoopExecutionLease
    {
        private readonly string _workspaceKey;
        private readonly WorkspaceHost _host;
        private readonly long _generation;
        private int _disposed;

        public ExecutionLease(string workspaceKey, WorkspaceHost host, string operationId, long generation)
        {
            _workspaceKey = workspaceKey;
            _host = host;
            OperationId = operationId;
            _generation = generation;
        }

        public string OperationId { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseLease(_workspaceKey, _host, OperationId, _generation);
            }
        }
    }

    private sealed class BusyOutcomeReservationLease : ICustomLoopExecutionLease
    {
        private readonly string _workspaceKey;
        private readonly WorkspaceHost _host;
        private readonly long _generation;
        private int _disposed;

        public BusyOutcomeReservationLease(string workspaceKey, WorkspaceHost host, string operationId, long generation)
        {
            _workspaceKey = workspaceKey;
            _host = host;
            OperationId = operationId;
            _generation = generation;
        }

        public string OperationId { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseBusyOutcomeReservation(_workspaceKey, _host, OperationId, _generation);
            }
        }
    }
}
