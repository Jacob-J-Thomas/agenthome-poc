using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;

namespace EmbodySense.Core.Application.Loops.Wait;

/// <summary>Breaks the composition cycle between the canonical sleep service and its exact Wait frontier continuation.</summary>
/// <remarks>The relay may be bound exactly once before background work starts. It owns no state or policy beyond that immutable delegation.</remarks>
public sealed class GovernedLoopWaitContinuationRelay : IGovernedLoopWakeContinuationPort
{
    private IGovernedLoopWakeContinuationPort? _target;
    private IGovernedLoopWakeContinuationPort? _retryTarget;

    /// <summary>Binds the sole canonical continuation target exactly once.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a target was already bound.</exception>
    public void Bind(IGovernedLoopWakeContinuationPort target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this) || Interlocked.CompareExchange(ref _target, target, null) is not null)
        {
            throw new InvalidOperationException("The governed Wait continuation relay may be bound exactly once to a different target.");
        }
    }

    /// <summary>Binds the sole canonical retry continuation target exactly once.</summary>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="target"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when a retry target was already bound.</exception>
    public void BindRetry(IGovernedLoopWakeContinuationPort target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ReferenceEquals(target, this) || Interlocked.CompareExchange(ref _retryTarget, target, null) is not null)
        {
            throw new InvalidOperationException("The governed Wait continuation relay may be bound exactly once to a retry target.");
        }
    }

    /// <inheritdoc />
    public Task<GovernedLoopWakeContinuationResult?> ContinueAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken = default)
        => Target(request)?.ContinueAsync(request, cancellationToken)
            ?? Task.FromResult<GovernedLoopWakeContinuationResult?>(
                new GovernedLoopWakeContinuationResult(
                    GovernedLoopWakeContinuationStatus.Unavailable,
                    EvidenceReference: "wait-continuation-not-composed"));

    /// <inheritdoc />
    public Task<GovernedLoopWakeContinuationResult?> ReconcileAsync(
        GovernedLoopWakeContinuationRequest request,
        CancellationToken cancellationToken = default)
        => Target(request)?.ReconcileAsync(request, cancellationToken)
            ?? Task.FromResult<GovernedLoopWakeContinuationResult?>(
                new GovernedLoopWakeContinuationResult(
                    GovernedLoopWakeContinuationStatus.Unavailable,
                    EvidenceReference: "wait-continuation-not-composed"));

    private IGovernedLoopWakeContinuationPort? Target(GovernedLoopWakeContinuationRequest request)
        => request.Checkpoint.Binding.WaitOperationId.StartsWith("retry-", StringComparison.Ordinal)
            ? Volatile.Read(ref _retryTarget)
            : Volatile.Read(ref _target);
}
