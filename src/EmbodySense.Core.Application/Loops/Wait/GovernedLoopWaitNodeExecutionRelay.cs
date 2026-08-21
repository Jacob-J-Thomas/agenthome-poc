using EmbodySense.Core.Application.Loops.Sequential.Models;
using EmbodySense.Core.Application.Loops.Wait.Models;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Breaks the composition cycle between the ordered runtime and its canonical Wait executor.</summary>
public sealed class GovernedLoopWaitNodeExecutionRelay : IGovernedLoopWaitNodeExecutor
{
    private IGovernedLoopWaitNodeExecutor? _target;

    /// <summary>Binds the sole canonical Wait executor exactly once.</summary>
    public void Bind(IGovernedLoopWaitNodeExecutor target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this) || Interlocked.CompareExchange(ref _target, target, null) is not null)
        {
            throw new InvalidOperationException("The governed Wait execution relay may be bound exactly once to a different target.");
        }
    }

    /// <inheritdoc />
    public Task<GovernedLoopWaitParkResult> ParkAsync(
        GovernedLoopSequentialNodeDispatchRequest request,
        CancellationToken cancellationToken = default)
        => Volatile.Read(ref _target)?.ParkAsync(request, cancellationToken)
            ?? Task.FromResult(new GovernedLoopWaitParkResult(
                GovernedLoopWaitParkResultStatus.Unavailable,
                Detail: "wait-executor-not-composed"));
}
