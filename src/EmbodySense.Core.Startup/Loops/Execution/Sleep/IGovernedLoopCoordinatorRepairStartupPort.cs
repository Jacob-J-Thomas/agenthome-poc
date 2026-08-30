using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Starts the one canonical background host after an already-durable coordinator repair disposition.</summary>
public interface IGovernedLoopCoordinatorRepairStartupPort
{
    /// <summary>Gets the exact coordinator identity owned by this canonical background host.</summary>
    string CoordinatorId { get; }

    /// <summary>Reaps a completed failed local session and performs one fresh repair-fenced start for the same coordinator.</summary>
    Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartAfterRepairAsync(CancellationToken cancellationToken = default);
}
