using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Loops.Sequential;

/// <summary>Durably records canonical sequential audits once under a caller-derived evidence identity.</summary>
public interface IGovernedLoopSequentialAuditRecorder
{
    /// <summary>
    /// Records one exact audit event or proves that the same operation and evidence were recorded previously.
    /// </summary>
    /// <remarks>
    /// An implementation must bind <paramref name="operationId"/>, <paramref name="evidenceHash"/>, and the complete
    /// <paramref name="auditEvent"/> atomically. Reuse of an operation identifier with any divergent identity or event
    /// returns <see cref="GovernedLoopSequentialAuditRecordStatus.Conflict"/>. An uncertain or unavailable durable result
    /// returns <see cref="GovernedLoopSequentialAuditRecordStatus.Unavailable"/>; callers must fail closed.
    /// </remarks>
    Task<GovernedLoopSequentialAuditRecordResult> RecordOnceAsync(
        string operationId,
        string evidenceHash,
        AuditEvent auditEvent,
        CancellationToken cancellationToken = default);
}
