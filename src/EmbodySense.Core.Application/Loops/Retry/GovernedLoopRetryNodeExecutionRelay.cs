using EmbodySense.Core.Application.Loops.Retry.Models;

namespace EmbodySense.Core.Application.Loops.Retry;

/// <summary>Breaks the composition cycle between the ordered runner and its canonical retry scheduler.</summary>
public sealed class GovernedLoopRetryNodeExecutionRelay : IGovernedLoopRetryNodeExecutor
{
    private IGovernedLoopRetryNodeExecutor? _target;

    /// <summary>Binds the sole retry scheduler exactly once before execution begins.</summary>
    public void Bind(IGovernedLoopRetryNodeExecutor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this) || Interlocked.CompareExchange(ref _target, target, null) is not null)
        {
            throw new InvalidOperationException("The governed retry node relay may be bound exactly once to a different target.");
        }
    }

    /// <inheritdoc />
    public Task<GovernedLoopRetryExecutionResult> ScheduleAsync(
        GovernedLoopRetryExecutionRequest request,
        CancellationToken cancellationToken = default)
        => Volatile.Read(ref _target)?.ScheduleAsync(request, cancellationToken)
            ?? Task.FromResult(new GovernedLoopRetryExecutionResult(
                GovernedLoopRetryExecutionStatus.Unavailable,
                null,
                null,
                "retry-scheduler-not-composed"));
}
