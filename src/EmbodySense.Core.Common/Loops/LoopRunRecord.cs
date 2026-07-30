using EmbodySense.Core.Common.Runtime;
using EmbodySense.Core.Common.Loops.Models;

namespace EmbodySense.Core.Common.Loops;

/// <summary>
/// Records the surface, trigger, timestamps, outcome, and metadata of one built-in loop invocation.
/// </summary>
/// <param name="SchemaVersion">The persisted schema version.</param>
/// <param name="RunId">The unique run identifier.</param>
/// <param name="LoopId">The owning loop identifier.</param>
/// <param name="RoleId">The workspace role identifier.</param>
/// <param name="Status">The status.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="Trigger">The trigger.</param>
/// <param name="StartedAtUtc">The started at UTC.</param>
/// <param name="CompletedAtUtc">The UTC terminal time, or <see langword="null"/> while nonterminal.</param>
/// <param name="FailureDetail">The human-readable terminal failure detail, or <see langword="null"/> when not applicable.</param>
/// <param name="Metadata">Additional metadata retained with the value.</param>
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
    /// <summary>
    /// Schema version required by the current built-in loop-run contract.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Creates a started run with no terminal timestamp or failure detail.
    /// </summary>
    /// <param name="runId">The run ID.</param>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="roleId">The role ID.</param>
    /// <param name="surface">The normalized interface/runtime surface.</param>
    /// <param name="trigger">A concrete loop trigger.</param>
    /// <param name="startedAtUtc">The UTC start time.</param>
    /// <param name="metadata">Optional metadata copied into the run, or <see langword="null"/> for an empty dictionary.</param>
    /// <returns>A version-1 run in <see cref="LoopRunStatus.Started"/> state.</returns>
    /// <exception cref="ArgumentException">Thrown when a required identity is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="surface"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="trigger"/> is unknown or undefined.</exception>
    public static LoopRunRecord Started(
        string runId,
        string loopId,
        string roleId,
        RuntimeSurfaceId surface,
        LoopTrigger trigger,
        DateTimeOffset startedAtUtc,
        Dictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(loopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleId);
        ArgumentNullException.ThrowIfNull(surface);
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
            surface.Id,
            trigger,
            startedAtUtc,
            null,
            null,
            metadata ?? []);
    }

    /// <summary>
    /// Returns a completed copy of this run.
    /// </summary>
    /// <param name="completedAtUtc">The UTC completion time.</param>
    /// <returns>A copy in <see cref="LoopRunStatus.Completed"/> state with failure detail cleared.</returns>
    public LoopRunRecord Complete(DateTimeOffset completedAtUtc)
    {
        return this with { Status = LoopRunStatus.Completed, CompletedAtUtc = completedAtUtc, FailureDetail = null };
    }

    /// <summary>
    /// Returns a failed copy of this run.
    /// </summary>
    /// <param name="completedAtUtc">The UTC failure time.</param>
    /// <param name="failureDetail">The non-empty failure explanation.</param>
    /// <returns>A copy in <see cref="LoopRunStatus.Failed"/> state with the supplied detail.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="failureDetail"/> is empty or whitespace.</exception>
    public LoopRunRecord Fail(DateTimeOffset completedAtUtc, string failureDetail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureDetail);
        return this with { Status = LoopRunStatus.Failed, CompletedAtUtc = completedAtUtc, FailureDetail = failureDetail };
    }

    /// <summary>
    /// Returns a cancelled copy of this run.
    /// </summary>
    /// <param name="completedAtUtc">The UTC cancellation time.</param>
    /// <param name="detail">The non-empty cancellation explanation.</param>
    /// <returns>A copy in <see cref="LoopRunStatus.Cancelled"/> state with the supplied detail.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="detail"/> is empty or whitespace.</exception>
    public LoopRunRecord Cancel(DateTimeOffset completedAtUtc, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        return this with { Status = LoopRunStatus.Cancelled, CompletedAtUtc = completedAtUtc, FailureDetail = detail };
    }
}
