using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;
using EmbodySense.Core.Common.CommandActions;
using EmbodySense.Core.Common.CommandActions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class CrashThenReplayCommandActionExecutor : IGovernedLoopCommandActionExecutor
{
    private readonly HashSet<string> _retainedOperations = new(StringComparer.Ordinal);

    internal int LaunchCount { get; private set; }

    internal List<GovernedLoopCommandActionExecutionRequest> Requests { get; } = [];

    public Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(GovernedLoopCommandActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        if (_retainedOperations.Add(request.AttemptOperationId))
        {
            LaunchCount++;
            throw new IOException("Simulated response loss after the retained command outcome.");
        }

        var output = CommandActionResultContract.Encode(CommandActionResultContract.Create(
            CommandActionResultStatus.Replayed,
            CommandActionResultOutcome.Succeeded,
            "command-outcome-" + new string('b', 64),
            1));
        return Task.FromResult(new GovernedLoopCommandActionExecutionResult(
            GovernedLoopCommandActionExecutionStatus.Completed,
            output,
            "The exact retained command Action outcome was replayed."));
    }
}
