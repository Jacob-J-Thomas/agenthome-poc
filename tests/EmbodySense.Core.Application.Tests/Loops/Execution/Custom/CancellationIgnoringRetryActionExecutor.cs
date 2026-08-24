using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class CancellationIgnoringRetryActionExecutor(bool commandAction) : IGovernedLoopWorkspaceActionExecutor, IGovernedLoopCommandActionExecutor
{
    private readonly bool _commandAction = commandAction;

    internal int RequestCount { get; private set; }

    public Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(GovernedLoopWorkspaceActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_commandAction)
        {
            throw new InvalidOperationException("The command-only cancellation-ignoring test executor received a workspace Action.");
        }

        RequestCount++;
        return Task.FromResult(RequestCount == 1
            ? new GovernedLoopWorkspaceActionExecutionResult(GovernedLoopWorkspaceActionExecutionStatus.Rejected, null, "The workspace Action was rejected before transport.")
            : new GovernedLoopWorkspaceActionExecutionResult(GovernedLoopWorkspaceActionExecutionStatus.Completed, WorkspaceActionResultContract.Encode(WorkspaceActionResultContract.Create(WorkspaceActionResultStatus.Committed, new string('a', 64), 1)), "The workspace Action outcome is durable."));
    }

    public Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(GovernedLoopCommandActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_commandAction)
        {
            throw new InvalidOperationException("The workspace-only cancellation-ignoring test executor received a command Action.");
        }

        RequestCount++;
        return Task.FromResult(RequestCount == 1
            ? new GovernedLoopCommandActionExecutionResult(GovernedLoopCommandActionExecutionStatus.Rejected, null, "The command Action was rejected before transport.")
            : new GovernedLoopCommandActionExecutionResult(GovernedLoopCommandActionExecutionStatus.Completed, CommandActionResultContract.Encode(CommandActionResultContract.Create(CommandActionResultStatus.Committed, CommandActionResultOutcome.Succeeded, new string('a', 64), 1)), "The command Action outcome is durable."));
    }
}
