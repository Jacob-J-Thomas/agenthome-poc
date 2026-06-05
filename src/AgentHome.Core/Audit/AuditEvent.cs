namespace AgentHome.Core.Audit;

public sealed record AuditEvent(
    string Actor,
    string Action,
    string Target,
    string Decision,
    string? TaskId = null,
    string? Detail = null)
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
