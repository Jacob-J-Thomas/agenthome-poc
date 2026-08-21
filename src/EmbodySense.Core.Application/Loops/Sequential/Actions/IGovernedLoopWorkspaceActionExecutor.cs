using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Actions;

/// <summary>Executes or resumes one exact admitted workspace Action through the canonical actuator protocol.</summary>
public interface IGovernedLoopWorkspaceActionExecutor
{
    /// <summary>Executes or safely resumes one exact graph Action without selecting frontier policy.</summary>
    Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(
        GovernedLoopWorkspaceActionExecutionRequest request,
        CancellationToken cancellationToken = default);
}
