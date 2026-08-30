using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Common.Loops.Execution.Sleep;

/// <summary>Evaluates whether immutable coordinator-repair readiness evidence authorizes every canonical work family.</summary>
public static class GovernedLoopCoordinatorRepairReadinessContract
{
    /// <summary>Determines whether all canonical local work families are ready for a fresh repair-bound acquisition.</summary>
    /// <param name="readiness">The immutable readiness evidence to evaluate.</param>
    /// <returns><see langword="true"/> only when every canonical family was observed ready.</returns>
    public static bool IsReady(GovernedLoopCoordinatorRepairReadiness? readiness)
        => readiness is not null
            && readiness.ScheduleReady
            && readiness.TriggerReady
            && readiness.WakeReady
            && readiness.HumanInputReady;
}
