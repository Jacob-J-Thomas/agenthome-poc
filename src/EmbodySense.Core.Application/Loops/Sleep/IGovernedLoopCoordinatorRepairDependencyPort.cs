using EmbodySense.Core.Common.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Sleep;

/// <summary>Reads trusted non-actuating readiness evidence for every canonical coordinator work family.</summary>
public interface IGovernedLoopCoordinatorRepairDependencyPort
{
    /// <summary>Reads one current workspace and coordinator-bound dependency readiness evidence object.</summary>
    Task<GovernedLoopCoordinatorRepairReadiness?> ReadAsync(
        string workspaceId,
        string coordinatorId,
        CancellationToken cancellationToken = default);
}
