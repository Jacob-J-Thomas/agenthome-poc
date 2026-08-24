using EmbodySense.Core.Application.Loops.Execution.Effects;
using EmbodySense.Core.Application.Loops.Execution.Effects.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Effects;

internal sealed class CapturingCommandActionEffectService(
    Func<GovernedLoopEffectAttemptRequest, GovernedLoopEffectAttemptExecutionResult> resultFactory) : IGovernedLoopEffectAttemptService
{
    private readonly Func<GovernedLoopEffectAttemptRequest, GovernedLoopEffectAttemptExecutionResult> _resultFactory = resultFactory ?? throw new ArgumentNullException(nameof(resultFactory));

    internal GovernedLoopEffectAttemptRequest? Request { get; private set; }

    public Task<GovernedLoopEffectAttemptExecutionResult> ExecuteAsync(
        GovernedLoopEffectAttemptRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Request = request;
        return Task.FromResult(_resultFactory(request));
    }
}
