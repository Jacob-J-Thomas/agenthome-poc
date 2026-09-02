using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Tests.Support;

namespace EmbodySense.CancellationHost.Persistence;

internal static class SequentialAuditCrossProcessHost
{
    private const int RecordThenExitCode = 91;
    private static readonly DateTimeOffset _timestamp = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    internal static async Task<int> RecordThenExitAsync(string workspaceRoot)
    {
        var evidenceHash = new string('a', 64);
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        var auditEvent = new AuditEvent(
            _timestamp,
            "loop-runtime",
            "node-outcome",
            "run-1",
            "succeeded",
            "Deterministic node outcome recorded.",
            new Dictionary<string, object?>());
        var result = await new AuditLog(new WorkspacePaths(workspaceRoot))
            .RecordOnceAsync(operationId, evidenceHash, auditEvent);
        if (result.Status != GovernedLoopSequentialAuditRecordStatus.Recorded)
        {
            return 3;
        }

        Environment.Exit(RecordThenExitCode);
        return RecordThenExitCode;
    }

    internal static async Task<int> AppendAsync(string workspaceRoot, string identity, string readyPath, string releasePath, string resultPath)
    {
        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyPath, releasePath);
        await new AuditLog(new WorkspacePaths(workspaceRoot)).AppendAsync(Event("append-" + identity));
        await CrossProcessMarkerProtocol.WriteResultAsync(resultPath, "appended");
        return 0;
    }

    internal static async Task<int> RecordAsync(string workspaceRoot, string readyPath, string releasePath, string resultPath)
    {
        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyPath, releasePath);
        var evidenceHash = new string('a', 64);
        var operationId = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(evidenceHash);
        var result = await new AuditLog(new WorkspacePaths(workspaceRoot)).RecordOnceAsync(operationId, evidenceHash, Event("node-outcome"));
        await CrossProcessMarkerProtocol.WriteResultAsync(resultPath, $"{result.Status}|{result.Detail}");
        return 0;
    }

    internal static async Task<int> HoldLockAsync(string lockPath, string readyPath, string releasePath)
    {
        using var ownership = CrossProcessExclusiveFileLock.Acquire(lockPath);
        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyPath, releasePath);
        return 0;
    }

    internal static async Task<int> AppendIncompleteTailAndHoldAsync(string workspaceRoot, string readyPath, string releasePath)
    {
        var paths = new WorkspacePaths(workspaceRoot);
        var lockPath = Path.Combine(paths.AuditPath, ".events.ndjson.mutation.lock");
        using var ownership = CrossProcessExclusiveFileLock.Acquire(lockPath);
        await using (var stream = new FileStream(paths.EventsLogPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync("{\"timestampUtc\":"u8.ToArray(), CancellationToken.None);
            stream.Flush(flushToDisk: true);
        }

        await CrossProcessMarkerProtocol.SignalReadyAndWaitForReleaseAsync(readyPath, releasePath);
        return 0;
    }

    private static AuditEvent Event(string action)
        => new(
            _timestamp,
            "loop-runtime",
            action,
            "run-1",
            "succeeded",
            "Deterministic audit event recorded.",
            new Dictionary<string, object?>());
}
