using EmbodySense.Core.Application.Loops.Sequential.Actions;
using EmbodySense.Core.Application.Loops.Sequential.Actions.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Custom;

internal sealed class BlockingCancellationAwareActionExecutor : IGovernedLoopWorkspaceActionExecutor, IGovernedLoopCommandActionExecutor
{
    internal int WorkspaceRequests { get; private set; }

    internal int CommandRequests { get; private set; }

    internal bool CancellationObserved { get; private set; }

    public async Task<GovernedLoopWorkspaceActionExecutionResult> ExecuteAsync(GovernedLoopWorkspaceActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        WorkspaceRequests++;
        await ObserveCancellationAsync(cancellationToken);
        throw new InvalidOperationException("The cancellation-aware workspace Action unexpectedly returned.");
    }

    public async Task<GovernedLoopCommandActionExecutionResult> ExecuteAsync(GovernedLoopCommandActionExecutionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CommandRequests++;
        await ObserveCancellationAsync(cancellationToken);
        throw new InvalidOperationException("The cancellation-aware command Action unexpectedly returned.");
    }

    private async Task ObserveCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancellationObserved = true;
            throw;
        }
    }
}
