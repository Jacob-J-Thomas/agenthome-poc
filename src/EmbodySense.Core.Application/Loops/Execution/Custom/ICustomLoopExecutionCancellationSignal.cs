using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopExecutionCancellationSignal
{
    void CancelActiveAttempt(string runId);
}
