using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Loops.Execution.Sleep;

/// <summary>Retains whether a bounded canonical Human Review recovery probe has established executable posture.</summary>
public sealed class HumanReviewRecoveryReadinessSignal
{
    private static readonly TimeSpan _defaultAggregateProbeInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan _maximumAggregateProbeInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan _minimumAggregateProbeInterval = TimeSpan.FromSeconds(1);
    private readonly Func<CancellationToken, Task<bool>>? _aggregateHealthProbe;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _aggregateProbeInterval;
    private long _lastAggregateProbeTimestamp;
    private int _aggregateProbeCompleted;
    private int _isExecutable;
    private int _recoveryInvalidated;

    /// <summary>Initializes a signal that optionally verifies all non-recovery Human Review dependencies.</summary>
    /// <param name="aggregateHealthProbe">The bounded read-only aggregate health probe, or null for recovery-only callers.</param>
    /// <param name="timeProvider">The monotonic clock used to bound repeated aggregate probes.</param>
    /// <param name="aggregateProbeInterval">The lazy steady-state revalidation interval.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the probe interval is outside the supported bound.</exception>
    public HumanReviewRecoveryReadinessSignal(
        Func<CancellationToken, Task<bool>>? aggregateHealthProbe = null,
        TimeProvider? timeProvider = null,
        TimeSpan? aggregateProbeInterval = null)
    {
        _aggregateHealthProbe = aggregateHealthProbe;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _aggregateProbeInterval = aggregateProbeInterval ?? _defaultAggregateProbeInterval;
        if (_aggregateProbeInterval < _minimumAggregateProbeInterval || _aggregateProbeInterval > _maximumAggregateProbeInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(aggregateProbeInterval));
        }
    }

    /// <summary>Gets whether the most recent non-cancelled probe or recovery pass was healthy.</summary>
    public bool IsExecutable => Volatile.Read(ref _isExecutable) != 0;

    internal Task ObserveAsync(HumanReviewRecoveryPassStatus status, CancellationToken cancellationToken)
        => ObserveAsync(status == HumanReviewRecoveryPassStatus.Current, cancellationToken);

    internal Task ObserveAsync(GovernedLoopLocalWorkResultStatus status, CancellationToken cancellationToken)
        => ObserveAsync(status is GovernedLoopLocalWorkResultStatus.Completed or GovernedLoopLocalWorkResultStatus.Empty, cancellationToken);

    private async Task ObserveAsync(bool recoveryIsCurrent, CancellationToken cancellationToken)
    {
        if (!recoveryIsCurrent)
        {
            Volatile.Write(ref _recoveryInvalidated, 1);
            Volatile.Write(ref _isExecutable, 0);
            return;
        }

        if (_aggregateHealthProbe is null)
        {
            Volatile.Write(ref _isExecutable, 1);
            return;
        }

        if (!TryReadTimestamp(out var timestamp))
        {
            return;
        }

        var recoveryRequiresProbe = Interlocked.Exchange(ref _recoveryInvalidated, 0) != 0;
        if (!recoveryRequiresProbe
            && Volatile.Read(ref _aggregateProbeCompleted) != 0)
        {
            if (!TryReadElapsedTime(Volatile.Read(ref _lastAggregateProbeTimestamp), timestamp, out var elapsed))
            {
                return;
            }

            if (elapsed < _aggregateProbeInterval)
            {
                return;
            }
        }

        try
        {
            var healthy = await _aggregateHealthProbe(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _isExecutable, healthy ? 1 : 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (recoveryRequiresProbe)
            {
                Volatile.Write(ref _recoveryInvalidated, 1);
            }

            throw;
        }
        catch
        {
            Volatile.Write(ref _isExecutable, 0);
        }

        Volatile.Write(ref _lastAggregateProbeTimestamp, timestamp);
        Volatile.Write(ref _aggregateProbeCompleted, 1);
    }

    private bool TryReadTimestamp(out long timestamp)
    {
        try
        {
            timestamp = _timeProvider.GetTimestamp();
            return true;
        }
        catch
        {
            Volatile.Write(ref _isExecutable, 0);
            timestamp = 0;
            return false;
        }
    }

    private bool TryReadElapsedTime(long startTimestamp, long endTimestamp, out TimeSpan elapsed)
    {
        try
        {
            elapsed = _timeProvider.GetElapsedTime(startTimestamp, endTimestamp);
            if (elapsed >= TimeSpan.Zero)
            {
                return true;
            }
        }
        catch
        {
        }

        Volatile.Write(ref _isExecutable, 0);
        elapsed = TimeSpan.Zero;
        return false;
    }
}
