using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;

namespace EmbodySense.Core.Startup.Audit;

public sealed class AuditTailReader
{
    public async Task<(string EventsLogPath, IReadOnlyList<AuditTailEvent> Events)> ReadTailAsync(string rootPath, int limit, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);
        var events = await new AuditLog(paths).ReadTailAsync(limit, cancellationToken);
        return (paths.EventsLogPath, events.Select(AuditTailEvent.FromAuditEvent).ToArray());
    }
}
