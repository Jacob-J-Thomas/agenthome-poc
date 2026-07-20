namespace EmbodySense.Core.Application.Loops.Execution.Custom;

public interface ICustomLoopInferenceAttemptExecutor
{
    Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null);
}
