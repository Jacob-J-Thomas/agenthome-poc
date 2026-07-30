namespace EmbodySense.Core.Common.Governance.Audit;

/// <summary>
/// Represents an audit event.
/// </summary>
/// <param name="TimestampUtc">The UTC event time.</param>
/// <param name="Actor">The actor.</param>
/// <param name="Action">The action.</param>
/// <param name="Target">The target.</param>
/// <param name="Outcome">The outcome.</param>
/// <param name="Detail">The detail.</param>
/// <param name="Metadata">Additional metadata retained with the value.</param>
public sealed record AuditEvent(
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    IReadOnlyDictionary<string, object?> Metadata)
{
    /// <summary>
    /// Creates an audit event.
    /// </summary>
    /// <param name="actor">The actor.</param>
    /// <param name="action">The action.</param>
    /// <param name="target">The target.</param>
    /// <param name="outcome">The outcome.</param>
    /// <param name="detail">The detail.</param>
    /// <param name="metadata">The metadata.</param>
    /// <returns>The audit event.</returns>
    public static AuditEvent Create(
        string actor,
        string action,
        string target,
        string outcome,
        string detail,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);

        return new AuditEvent(
            DateTimeOffset.UtcNow,
            actor,
            action,
            target,
            outcome,
            detail,
            metadata ?? new Dictionary<string, object?>());
    }
}
