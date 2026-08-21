using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Executes one exact admitted Wait activation selected by the canonical ordered runtime.</summary>
public interface IGovernedLoopWaitNodeExecutor
{
    /// <summary>Parks one exact Running Wait activation and publishes its durable sleeping checkpoint.</summary>
    Task<GovernedLoopWaitParkResult> ParkAsync(
        GovernedLoopSequentialNodeDispatchRequest request,
        CancellationToken cancellationToken = default);
}
