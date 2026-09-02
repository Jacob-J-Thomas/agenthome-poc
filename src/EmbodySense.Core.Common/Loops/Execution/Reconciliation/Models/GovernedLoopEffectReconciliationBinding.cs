namespace EmbodySense.Core.Common.Loops.Execution.Reconciliation.Models;

/// <summary>Binds reconciliation to one exact workspace, execution visit, and current effect-attempt version.</summary>
/// <param name="SchemaVersion">The binding schema, which must be 1.</param>
/// <param name="WorkspaceId">The exact admitted workspace identity.</param>
/// <param name="Execution">The exact run, immutable revision, and execution generation.</param>
/// <param name="NodeId">The exact originating node.</param>
/// <param name="ActivationOrdinal">The exact zero-based frontier activation.</param>
/// <param name="VisitOrdinal">The exact positive visit of the node.</param>
/// <param name="NodeAttempt">The exact positive node attempt.</param>
/// <param name="EffectId">The stable effect identity.</param>
/// <param name="OperationId">The stable idempotency operation identity.</param>
/// <param name="EffectGeneration">The exact positive effect generation.</param>
/// <param name="IntentHash">The exact canonical intent hash.</param>
/// <param name="CurrentAttemptHash">The exact content hash of the authoritative reconciliation-required attempt.</param>
/// <param name="ContentHash">The canonical hash of this binding except this field.</param>
public sealed record GovernedLoopEffectReconciliationBinding(
    int SchemaVersion,
    string WorkspaceId,
    GovernedLoopExecutionBinding Execution,
    string NodeId,
    int ActivationOrdinal,
    int VisitOrdinal,
    int NodeAttempt,
    string EffectId,
    string OperationId,
    long EffectGeneration,
    string IntentHash,
    string CurrentAttemptHash,
    string ContentHash)
{
    /// <summary>Gets a defensively reconstructed execution binding.</summary>
    public GovernedLoopExecutionBinding Execution { get; } = Execution is null
        ? null!
        : GovernedLoopExecutionBinding.Create(Execution.SchemaVersion, Execution.RunId, Execution.Revision, Execution.ExecutionGeneration);
}
