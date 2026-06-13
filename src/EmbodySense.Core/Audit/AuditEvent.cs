namespace EmbodySense.Core.Audit;

public sealed record AuditEvent(
    DateTimeOffset TimestampUtc,
    string Actor,
    string Action,
    string Target,
    string Outcome,
    string Detail,
    IReadOnlyDictionary<string, object?> Metadata)
{
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
