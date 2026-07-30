using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

internal sealed class ActiveAttempt
{
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _competingCancellationToken;
    private int _signalQueued;
    private int _routedSignalDelivered;

    public ActiveAttempt(CancellationTokenSource cancellation, CancellationToken competingCancellationToken, long generation)
    {
        _cancellation = cancellation;
        _competingCancellationToken = competingCancellationToken;
        Generation = generation;
    }

    public long Generation { get; }

    public TaskCompletionSource<CustomLoopAttemptCancellationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Signal()
    {
        if (Interlocked.Exchange(ref _signalQueued, 1) == 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(static attempt => attempt.DeliverSignal(), this, preferLocal: false);
        }
    }

    public bool CanConfirmProviderInterruption(CancellationToken observedCancellationToken)
    {
        return Volatile.Read(ref _routedSignalDelivered) != 0
            && !_competingCancellationToken.IsCancellationRequested
            && observedCancellationToken.CanBeCanceled
            && observedCancellationToken == _cancellation.Token
            && _cancellation.IsCancellationRequested;
    }

    public void ConfirmProviderInterruption()
    {
        Completion.TrySetResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, "The provider attempt observed the routed cancellation signal."));
    }

    public void CompleteWithoutConfirmedInterruption()
    {
        Completion.TrySetResult(CreateUnconfirmedResult());
    }

    public CustomLoopAttemptCancellationResult CreateUnconfirmedResult()
    {
        if (Volatile.Read(ref _routedSignalDelivered) != 0 && !_competingCancellationToken.IsCancellationRequested)
        {
            return new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.SignalDelivered, "The cancellation signal was delivered, but the active operation completed or the acknowledgement window elapsed without confirmed provider interruption.");
        }

        var status = Volatile.Read(ref _signalQueued) == 0 ? CustomLoopAttemptCancellationStatus.NoActiveAttempt : CustomLoopAttemptCancellationStatus.OwnerUnavailable;
        var detail = status == CustomLoopAttemptCancellationStatus.NoActiveAttempt
            ? "The active operation completed before cancellation was routed."
            : _competingCancellationToken.IsCancellationRequested
                ? "A caller or deadline cancellation competed with the routed signal, so routed delivery could not be proved."
                : "The cancellation signal was queued, but delivery was not observed before the active operation completed or the acknowledgement window elapsed.";
        return new CustomLoopAttemptCancellationResult(status, detail);
    }

    public void CompleteOwnerUnavailable()
    {
        Completion.TrySetResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.OwnerUnavailable, "The workspace-host owner exited before provider interruption was confirmed."));
    }

    private void DeliverSignal()
    {
        try
        {
            if (_cancellation.IsCancellationRequested || _competingCancellationToken.IsCancellationRequested)
            {
                return;
            }

            Volatile.Write(ref _routedSignalDelivered, 1);
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _routedSignalDelivered, 0);
            CompleteWithoutConfirmedInterruption();
        }
        catch (AggregateException)
        {
            // The cancellation state is already visible even when a provider callback fails.
        }
    }
}
