using EmbodySense.Core.Application.Loops.Execution.Custom.Models;
namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Dispatches one request-bound custom-loop inference attempt.
/// </summary>
public interface ICustomLoopInferenceAttemptExecutor
{
    /// <summary>
    /// Executes one attempt and returns provider output plus observed tool evidence.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="providerRequestStarted">A callback invoked at the irreversible provider-dispatch boundary.</param>
    /// <returns>The request-bound provider result and tool-consumption evidence.</returns>
    Task<CustomLoopInferenceAttemptResult> ExecuteAsync(CustomLoopInferenceAttemptRequest request, CancellationToken cancellationToken = default, Action? providerRequestStarted = null);
}
