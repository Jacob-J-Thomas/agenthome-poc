using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class RecordingCapabilityAuditLog : IAuditLog
{
    internal List<AuditEvent> Events { get; } = [];

    public Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(auditEvent);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AuditEvent>>(Events.TakeLast(limit).ToArray());
}
