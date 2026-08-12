using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;

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
}
