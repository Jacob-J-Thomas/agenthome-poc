using EmbodySense.Core.Startup.Audit;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;

namespace EmbodySense.Core.Startup.Audit;

/// <summary>
/// Exposes a bounded, interface-safe projection of the workspace audit tail.
/// </summary>
public sealed class AuditTailReader
{
    /// <summary>
    /// Reads successfully deserialized events from the last bounded set of nonblank audit lines.
    /// </summary>
    /// <param name="rootPath">The workspace root, normalized to an absolute path.</param>
    /// <param name="limit">The maximum number of nonblank tail lines to inspect; must be positive.</param>
    /// <param name="cancellationToken">The token used to cancel file reading.</param>
    /// <returns>
    /// A task whose result contains the canonical audit path and up to <paramref name="limit"/>
    /// events in file order. Malformed tail lines are skipped without backfilling older events.
    /// </returns>
    /// <remarks>
    /// A missing file, or an inaccessible path whose existence probe reports false, produces an
    /// empty event list. Cancellation and failures after the file is opened propagate.
    /// </remarks>
    public async Task<(string EventsLogPath, IReadOnlyList<AuditTailEvent> Events)> ReadTailAsync(string rootPath, int limit, CancellationToken cancellationToken = default)
    {
        var paths = new WorkspacePaths(rootPath);
        var events = await new AuditLog(paths).ReadTailAsync(limit, cancellationToken);
        return (paths.EventsLogPath, events.Select(AuditTailEvent.FromAuditEvent).ToArray());
    }
}
