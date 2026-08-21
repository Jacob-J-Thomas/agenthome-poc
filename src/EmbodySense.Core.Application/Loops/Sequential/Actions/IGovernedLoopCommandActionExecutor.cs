using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

namespace EmbodySense.Core.Application.Loops.Sequential.Actions;

/// <summary>Executes or resumes one exact admitted structured command Action through the canonical actuator protocol.</summary>
public interface IGovernedLoopCommandActionExecutor
{
    /// <summary>Executes or safely resumes one exact graph command Action without selecting frontier policy.</summary>
    Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(
        GovernedLoopCommandActionExecutionRequest request,
        CancellationToken cancellationToken = default);
}
