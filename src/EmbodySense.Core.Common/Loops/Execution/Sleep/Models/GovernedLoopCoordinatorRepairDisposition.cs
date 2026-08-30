namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records one immutable authenticated authorization to supersede one exact failed coordinator generation.</summary>
/// <remarks>
/// This evidence never edits the failed ownership, lifecycle, heartbeat, or failure artifacts it names. A subsequent
/// fresh ownership acquisition must still fence itself against the retained failed generation and this disposition.
/// </remarks>
/// <param name="SchemaVersion">The disposition schema version, which must be 1.</param>
/// <param name="WorkspaceId">The stable workspace identity that owns the coordinator.</param>
/// <param name="CoordinatorId">The exact stable coordinator identity.</param>
/// <param name="OperationId">The caller-held idempotency identity for this repair.</param>
/// <param name="ActorId">The authenticated current operator that authorized this repair.</param>
/// <param name="FailedOwnership">The exact failed ownership generation being superseded.</param>
/// <param name="TerminalLifecycleHash">The exact terminal failed lifecycle hash.</param>
/// <param name="LatestHeartbeatHash">The exact retained heartbeat head used to prove lease expiry before acquisition.</param>
/// <param name="LatestFailureHash">The exact latest failure hash explaining the failed generation.</param>
/// <param name="DependencyReadiness">The trusted all-family readiness evidence accepted for this repair.</param>
/// <param name="RecordedAtUtc">The trusted UTC instant at which the operator authorization was recorded.</param>
/// <param name="ContentHash">The canonical hash over this disposition except this field.</param>
public sealed record GovernedLoopCoordinatorRepairDisposition(
    int SchemaVersion,
    string WorkspaceId,
    string CoordinatorId,
    string OperationId,
    string ActorId,
    GovernedLoopCoordinatorOwnership FailedOwnership,
    string TerminalLifecycleHash,
    string LatestHeartbeatHash,
    string LatestFailureHash,
    GovernedLoopCoordinatorRepairReadiness DependencyReadiness,
    DateTimeOffset RecordedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental repair-disposition schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;

    /// <summary>Gets a defensive copy of the failed ownership evidence.</summary>
    public GovernedLoopCoordinatorOwnership FailedOwnership { get; } = FailedOwnership with { };
}
