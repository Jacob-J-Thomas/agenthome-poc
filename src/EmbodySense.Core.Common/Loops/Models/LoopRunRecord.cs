namespace EmbodySense.Core.Common.Loops.Models;

public sealed record LoopRunRecord(
    int SchemaVersion,
    string RunId,
    string LoopId,
    string RoleId,
    LoopRunStatus Status,
    string Surface,
    LoopTrigger Trigger,
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
        LoopTrigger trigger,
        DateTimeOffset startedAtUtc,
        Dictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentException.ThrowIfNullOrWhiteSpace(surface);
        if (!Enum.IsDefined(trigger) || trigger == LoopTrigger.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Choose a concrete loop trigger.");
        }

        return new LoopRunRecord(
            CurrentSchemaVersion,
            runId,
            loopId,
            roleId,
            LoopRunStatus.Started,
            surface,
            trigger,
            startedAtUtc,
            null,
            null,
            metadata ?? []);
    }

    public LoopRunRecord Complete(DateTimeOffset completedAtUtc)
    {
        return this with { Status = LoopRunStatus.Completed, CompletedAtUtc = completedAtUtc, FailureDetail = null };
    }

    public LoopRunRecord Fail(DateTimeOffset completedAtUtc, string failureDetail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        return this with { Status = LoopRunStatus.Failed, CompletedAtUtc = completedAtUtc, FailureDetail = failureDetail };
    }

    public LoopRunRecord Cancel(DateTimeOffset completedAtUtc, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return this with { Status = LoopRunStatus.Cancelled, CompletedAtUtc = completedAtUtc, FailureDetail = detail };
    }
}
