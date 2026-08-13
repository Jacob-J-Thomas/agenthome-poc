using EmbodySense.Core.Common.Loops.Execution.Wait.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Wait;

/// <summary>Retains the append-only evidence for one exact Wait activation in the canonical run artifact.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="ActivationOrdinal">The exact append-once activation identity in the canonical frontier.</param>
/// <param name="NodeId">The immutable admitted graph-node identity.</param>
/// <param name="NodeVisitOrdinal">The exact visit identity for this activation.</param>
/// <param name="CycleId">The exact cycle identity, when the activation belongs to a bounded cycle.</param>
/// <param name="CycleIteration">The exact bounded cycle iteration, present exactly with <paramref name="CycleId"/>.</param>
/// <param name="WaitAttempt">The exact attempt retained across park and continuation.</param>
/// <param name="WaitOperationId">The exact attempt idempotency identity retained across park and continuation.</param>
/// <param name="Condition">The immutable condition admitted from the pinned graph revision.</param>
/// <param name="ParkedAtUtc">The stable UTC instant at which the Waiting frontier became durable.</param>
/// <param name="ParkedFrontierVersion">The exact frontier version that first committed this activation as Waiting.</param>
/// <param name="ParkedFrontierHash">The exact frontier hash paired with <paramref name="ParkedFrontierVersion"/>.</param>
/// <param name="ParkEvidence">The exact checkpoint publication evidence, when attached.</param>
/// <param name="ContinuationEvidence">The exact Waiting-to-Running continuation evidence, when committed.</param>
/// <param name="ContentHash">The canonical hash over every preceding field.</param>
public sealed record GovernedLoopWaitExecutionEvidence(
    int SchemaVersion,
    int ActivationOrdinal,
    string NodeId,
    int NodeVisitOrdinal,
    string? CycleId,
    int? CycleIteration,
    int WaitAttempt,
    string WaitOperationId,
    GovernedLoopWaitCondition Condition,
    DateTimeOffset ParkedAtUtc,
    long ParkedFrontierVersion,
    string ParkedFrontierHash,
    GovernedLoopWaitParkEvidence? ParkEvidence,
    GovernedLoopWaitContinuationEvidence? ContinuationEvidence,
    string ContentHash)
{
    private GovernedLoopWaitCondition? _condition = GovernedLoopWaitContractCopy.Copy(Condition);
    private GovernedLoopWaitParkEvidence? _parkEvidence = GovernedLoopWaitContractCopy.Copy(ParkEvidence);
    private GovernedLoopWaitContinuationEvidence? _continuationEvidence = GovernedLoopWaitContractCopy.Copy(ContinuationEvidence);

    /// <summary>Gets the only supported experimental evidence schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopWaitContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the immutable admitted Wait condition.</summary>
    public GovernedLoopWaitCondition Condition
    {
        get => _condition!;
        init => _condition = GovernedLoopWaitContractCopy.Copy(value);
    }

    /// <summary>Gets a defensive copy of the immutable park evidence, when checkpoint publication is retained.</summary>
    public GovernedLoopWaitParkEvidence? ParkEvidence
    {
        get => _parkEvidence;
        init => _parkEvidence = GovernedLoopWaitContractCopy.Copy(value);
    }

    /// <summary>Gets a defensive copy of the immutable continuation evidence, when the activation has resumed.</summary>
    public GovernedLoopWaitContinuationEvidence? ContinuationEvidence
    {
        get => _continuationEvidence;
        init => _continuationEvidence = GovernedLoopWaitContractCopy.Copy(value);
    }
}
