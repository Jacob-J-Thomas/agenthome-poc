namespace EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

/// <summary>Records trusted current readiness evidence for every canonical local coordinator work family.</summary>
/// <param name="SchemaVersion">The evidence schema version, which must be 1.</param>
/// <param name="WorkspaceId">The stable workspace identity whose dependencies were inspected.</param>
/// <param name="CoordinatorId">The exact coordinator whose dependencies were inspected.</param>
/// <param name="ScheduleReady">Whether the schedule work family is currently safe to admit.</param>
/// <param name="TriggerReady">Whether the trigger work family is currently safe to admit.</param>
/// <param name="WakeReady">Whether the wake work family is currently safe to admit.</param>
/// <param name="HumanInputReady">Whether the Human Input work family is currently safe to admit.</param>
/// <param name="EvaluatedAtUtc">The trusted UTC instant at which the dependencies were inspected.</param>
/// <param name="ContentHash">The canonical hash over this readiness evidence except this field.</param>
public sealed record GovernedLoopCoordinatorRepairReadiness(
    int SchemaVersion,
    string WorkspaceId,
    string CoordinatorId,
    bool ScheduleReady,
    bool TriggerReady,
    bool WakeReady,
    bool HumanInputReady,
    DateTimeOffset EvaluatedAtUtc,
    string ContentHash)
{
    /// <summary>Gets the only supported experimental readiness schema version.</summary>
    public const int CurrentSchemaVersion = GovernedLoopSleepContractLimits.CurrentSchemaVersion;
}
