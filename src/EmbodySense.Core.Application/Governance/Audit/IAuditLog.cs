using EmbodySense.Core.Common.Governance.Audit.Models;

namespace EmbodySense.Core.Application.Governance.Audit;

public interface IAuditLog
{
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default);
}
