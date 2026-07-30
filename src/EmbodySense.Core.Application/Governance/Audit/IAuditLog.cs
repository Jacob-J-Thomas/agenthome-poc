using EmbodySense.Core.Common.Governance.Audit;

namespace EmbodySense.Core.Application.Governance.Audit;

/// <summary>
/// Persists append-only governance events and exposes a bounded chronological tail.
/// </summary>
public interface IAuditLog
{
    /// <summary>
    /// Appends one immutable audit event.
    /// </summary>
    /// <param name="auditEvent">The event to persist.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the newest events in chronological order.
    /// </summary>
    /// <param name="limit">The positive maximum number of events to return.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>At most <paramref name="limit"/> events ordered from oldest to newest.</returns>
    Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default);
}
