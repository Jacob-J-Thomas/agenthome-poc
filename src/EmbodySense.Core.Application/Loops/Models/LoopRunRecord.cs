namespace EmbodySense.Core.Application.Loops.Models;

public sealed record LoopRunRecord(
    int SchemaVersion,
    string RunId,
    string LoopId,
    string RoleId,
    string Status,
    string Surface,
    string Trigger,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? FailureDetail,
    Dictionary<string, string> Metadata)
{
    public const int CurrentSchemaVersion = 1;

    public static LoopRunRecord Started(
        string runId,
        string loopId,
        string roleId,
        string surface,
        string trigger,
        DateTimeOffset startedAtUtc,
        Dictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);

        return new LoopRunRecord(
            CurrentSchemaVersion,
            runId,
            loopId,
            roleId,
            "started",
            surface,
            trigger,
            startedAtUtc,
            null,
            null,
            metadata ?? []);
    }
}
