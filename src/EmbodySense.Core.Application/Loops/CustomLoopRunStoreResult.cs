using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

public sealed record CustomLoopRunStoreResult(
    CustomLoopRunStoreStatus Status,
    CustomLoopRunRecord? Run,
    CustomLoopRunConflict? Conflict)
{
    public static CustomLoopRunStoreResult Created(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.Created, run, null);

    public static CustomLoopRunStoreResult Updated(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.Updated, run, null);

    public static CustomLoopRunStoreResult AlreadyCreated(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.AlreadyCreated, run, null);

    public static CustomLoopRunStoreResult VersionConflict(CustomLoopRunRecord run, int expectedLifecycleVersion)
    {
        var conflict = new CustomLoopRunConflict(run.Id, expectedLifecycleVersion, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.Conflict, null, conflict);
    }

    public static CustomLoopRunStoreResult NotFound() => new(CustomLoopRunStoreStatus.NotFound, null, null);

    public static CustomLoopRunStoreResult LimitExceeded() => new(CustomLoopRunStoreStatus.LimitExceeded, null, null);

    public static CustomLoopRunStoreResult OperationConflict(CustomLoopRunRecord run)
    {
        var conflict = new CustomLoopRunConflict(run.Id, 0, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.OperationConflict, null, conflict);
    }

    public static CustomLoopRunStoreResult NonterminalRunExists(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.NonterminalRunExists, run, null);

    public static CustomLoopRunStoreResult TerminalImmutable(CustomLoopRunRecord run, int expectedLifecycleVersion)
    {
        var conflict = new CustomLoopRunConflict(run.Id, expectedLifecycleVersion, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.TerminalImmutable, null, conflict);
    }
}
