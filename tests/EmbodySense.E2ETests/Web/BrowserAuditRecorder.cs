using EmbodySense.Core.Application.Loops.Sequential;
using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.E2ETests.Web;

internal sealed class BrowserAuditRecorder : IGovernedLoopSequentialAuditRecorder
{
    public Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(string operationId, string evidenceHash, AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GovernedLoopSequentialAuditRecordResult(GovernedLoopSequentialAuditRecordStatus.Recorded, "recorded"));
    }
}
