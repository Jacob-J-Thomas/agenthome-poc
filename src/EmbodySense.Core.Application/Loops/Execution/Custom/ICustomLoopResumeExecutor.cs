using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopResumeExecutor
{
    Task<CustomLoopOrderedRunResult> ResumeAsync(CustomLoopResumeExecutionRequest request, CancellationToken cancellationToken = default);
}
