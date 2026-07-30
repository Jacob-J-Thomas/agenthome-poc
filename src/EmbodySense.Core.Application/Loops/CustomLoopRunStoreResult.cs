using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops;

/// <summary>
/// Represents a custom loop run store result.
/// </summary>
/// <param name="Status">The status.</param>
/// <param name="Run">The run.</param>
/// <param name="Conflict">The conflict.</param>
public sealed record CustomLoopRunStoreResult(
    CustomLoopRunStoreStatus Status,
    CustomLoopRunRecord? Run,
    CustomLoopRunConflict? Conflict)
{
    /// <summary>
    /// Creates a successful run-creation result.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult Created(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.Created, run, null);

    /// <summary>
    /// Creates a custom loop run store result representing updated.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult Updated(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.Updated, run, null);

    /// <summary>
    /// Creates a custom loop run store result representing already created.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult AlreadyCreated(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.AlreadyCreated, run, null);

    /// <summary>
    /// Creates a custom loop run store result representing version conflict.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="expectedLifecycleVersion">The expected lifecycle version.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult VersionConflict(CustomLoopRunRecord run, int expectedLifecycleVersion)
    {
        var conflict = new CustomLoopRunConflict(run.Id, expectedLifecycleVersion, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.Conflict, null, conflict);
    }

    /// <summary>
    /// Creates a custom loop run store result representing not found.
    /// </summary>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult NotFound() => new(CustomLoopRunStoreStatus.NotFound, null, null);

    /// <summary>
    /// Creates a custom loop run store result representing limit exceeded.
    /// </summary>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult LimitExceeded() => new(CustomLoopRunStoreStatus.LimitExceeded, null, null);

    /// <summary>
    /// Creates a custom loop run store result representing operation conflict.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult OperationConflict(CustomLoopRunRecord run)
    {
        var conflict = new CustomLoopRunConflict(run.Id, 0, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.OperationConflict, null, conflict);
    }

    /// <summary>
    /// Creates a custom loop run store result representing nonterminal run exists.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult NonterminalRunExists(CustomLoopRunRecord run) => new(CustomLoopRunStoreStatus.NonterminalRunExists, run, null);

    /// <summary>
    /// Creates a custom loop run store result representing terminal immutable.
    /// </summary>
    /// <param name="run">The run.</param>
    /// <param name="expectedLifecycleVersion">The expected lifecycle version.</param>
    /// <returns>The custom loop run store result.</returns>
    public static CustomLoopRunStoreResult TerminalImmutable(CustomLoopRunRecord run, int expectedLifecycleVersion)
    {
        var conflict = new CustomLoopRunConflict(run.Id, expectedLifecycleVersion, run.LifecycleVersion, run.Status, run.UpdatedAtUtc);
        return new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.TerminalImmutable, null, conflict);
    }
}
