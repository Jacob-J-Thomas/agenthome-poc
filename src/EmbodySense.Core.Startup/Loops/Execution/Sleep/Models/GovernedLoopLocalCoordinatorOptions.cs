using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

/// <summary>Configures one bounded single-host background coordinator.</summary>
/// <param name="CoordinatorId">The stable workspace-local coordinator identity.</param>
/// <param name="OwnerId">The unique process-instance owner identity.</param>
/// <param name="CycleInterval">The positive delay between bounded work cycles.</param>
/// <param name="HeartbeatInterval">The positive delay between ownership renewals.</param>
/// <param name="OwnershipLeaseDuration">The exclusive coordinator lease duration.</param>
/// <param name="MaximumItemsPerFamilyPerCycle">The maximum one-shot attempts admitted for each family in one cycle.</param>
public sealed record GovernedLoopLocalCoordinatorOptions(
    string CoordinatorId,
    string OwnerId,
    TimeSpan CycleInterval,
    TimeSpan HeartbeatInterval,
    TimeSpan OwnershipLeaseDuration,
    int MaximumItemsPerFamilyPerCycle)
{
    /// <summary>Gets the largest admitted per-family cycle quota.</summary>
    public const int MaximumPerFamilyCycleQuota = TriggerWorkerLimits.MaxRecentLoopIds;
}
