using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class QueueCommandActionExecutor(params GovernedLoopCommandActionExecutionResult[] outcomes) : IGovernedLoopCommandActionExecutor
{
    private readonly Queue<GovernedLoopCommandActionExecutionResult> _outcomes = new(outcomes);

    internal List<GovernedLoopCommandActionExecutionRequest> Requests { get; } = [];

    public Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(GovernedLoopCommandActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_outcomes.Dequeue());
    }
}
