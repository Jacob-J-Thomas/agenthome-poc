using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Binds one sleeping wait to an exact published execution-frontier visit and attempt.</summary>
/// <param name="Execution">The canonical run, revision, and replacement-generation binding.</param>
/// <param name="Publication">The exact published revision admitted for the run.</param>
/// <param name="FrontierVersion">The exact optimistic frontier version released by the sleep publication.</param>
/// <param name="FrontierHash">The canonical hash of that exact frontier.</param>
/// <param name="ActivationOrdinal">The exact zero-based activation ordinal.</param>
/// <param name="CycleId">The optional exact cycle identity; present exactly when <paramref name="CycleIteration"/> is present.</param>
/// <param name="CycleIteration">The optional positive cycle iteration.</param>
/// <param name="NodeId">The exact graph-node identity.</param>
/// <param name="NodeVisitOrdinal">The exact positive visit ordinal for that node.</param>
/// <param name="WaitAttempt">The exact positive wait-attempt number.</param>
/// <param name="WaitOperationId">The stable idempotency identity of that wait attempt.</param>
public sealed record GovernedLoopSleepBinding(
    GovernedLoopExecutionBinding Execution,
    GovernedLoopRevisionPublicationPin Publication,
    long FrontierVersion,
    string FrontierHash,
    int ActivationOrdinal,
    string? CycleId,
    int? CycleIteration,
    string NodeId,
    int NodeVisitOrdinal,
    int WaitAttempt,
    string WaitOperationId)
{
    /// <summary>Gets a defensive copy of the canonical execution binding.</summary>
    public GovernedLoopExecutionBinding Execution { get; } = GovernedLoopSleepContractCopy.Copy(Execution);

    /// <summary>Gets a defensive copy of the exact publication pin.</summary>
    public GovernedLoopRevisionPublicationPin Publication { get; } = GovernedLoopSleepContractCopy.Copy(Publication);
}
