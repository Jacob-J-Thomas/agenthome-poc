using EmbodySense.Core.Startup.Loops.Execution.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>Activates composition-owned execution dependencies after workspace-host recovery is complete.</summary>
internal interface ICustomLoopExecutionActivation
{
    Task<CustomLoopExecutionActivationResult> ActivateAsync(CancellationToken cancellationToken = default);
}
