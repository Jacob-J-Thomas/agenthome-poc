using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Starts the one canonical background host after an already-durable coordinator repair disposition.</summary>
public interface IGovernedLoopCoordinatorRepairStartupPort
{
    /// <summary>Performs the existing fresh fenced background start without changing the submitted repair disposition.</summary>
    Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartAsync(CancellationToken cancellationToken = default);
}
