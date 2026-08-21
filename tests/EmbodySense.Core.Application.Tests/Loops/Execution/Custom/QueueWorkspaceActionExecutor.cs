using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class QueueWorkspaceActionExecutor(params GovernedLoopWorkspaceActionExecutionResult[] outcomes) : IGovernedLoopWorkspaceActionExecutor
{
    private readonly Queue<GovernedLoopWorkspaceActionExecutionResult> _outcomes = new(outcomes);

    internal List<GovernedLoopWorkspaceActionExecutionRequest> Requests { get; } = [];

    public Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(GovernedLoopWorkspaceActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(_outcomes.Dequeue());
    }
}
