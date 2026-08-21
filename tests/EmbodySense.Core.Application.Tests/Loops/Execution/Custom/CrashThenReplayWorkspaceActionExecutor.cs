using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class CrashThenReplayWorkspaceActionExecutor : IGovernedLoopWorkspaceActionExecutor
{
    private readonly HashSet<string> _retainedOperations = new(StringComparer.Ordinal);

    internal List<GovernedLoopWorkspaceActionExecutionRequest> Requests { get; } = [];

    internal int MutationCount { get; private set; }

    public Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(GovernedLoopWorkspaceActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (_retainedOperations.Add(request.AttemptOperationId))
        {
            MutationCount++;
            throw new IOException("Simulated process loss after the exact workspace mutation became durable.");
        }

        var output = WorkspaceActionResultContract.Encode(WorkspaceActionResultContract.Create(WorkspaceActionResultStatus.Replayed, "after-" + new string('a', 64), 1));
        return Task.FromResult(new GovernedLoopWorkspaceActionExecutionResult(GovernedLoopWorkspaceActionExecutionStatus.Completed, output, "The exact retained workspace Action outcome was replayed."));
    }
}
