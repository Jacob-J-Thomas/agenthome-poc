using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Tracks routed cancellation delivery and acknowledgement for one provider attempt generation.
/// </summary>
internal sealed class ActiveAttempt
{
    private readonly CancellationTokenSource _cancellation;
    private readonly CancellationToken _competingCancellationToken;
    private int _signalRequested;
    private int _routedSignalDelivered;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActiveAttempt"/> type.
    /// </summary>
    /// <param name="cancellation">The cancellation.</param>
    /// <param name="competingCancellationToken">The competing cancellation token.</param>
    /// <param name="generation">The generation.</param>
    public ActiveAttempt(CancellationTokenSource cancellation, CancellationToken competingCancellationToken, long generation)
    {
        _cancellation = cancellation;
        _competingCancellationToken = competingCancellationToken;
        Generation = generation;
    }

    /// <summary>
    /// Gets the generation used to reject stale registration completion.
    /// </summary>
    /// <value>The generation.</value>
    public long Generation { get; }

    /// <summary>
    /// Gets the asynchronous cancellation acknowledgement completed by the provider-attempt owner.
    /// </summary>
    /// <value>The task completion source.</value>
    public TaskCompletionSource<CustomLoopAttemptCancellationResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Delivers at most one cancellation signal without invoking callbacks under the host lock.
    /// </summary>
    public void Signal()
    {
        if (Interlocked.Exchange(ref _signalRequested, 1) != 0)
        {
            return;
        }

        DeliverSignal();
    }

    /// <summary>
    /// Determines whether the observed cancellation token can confirm provider interruption.
    /// </summary>
    /// <param name="observedCancellationToken">The observed cancellation token.</param>
    /// <returns><see langword="true"/> when can confirm provider interruption; otherwise, <see langword="false"/>.</returns>
    public bool CanConfirmProviderInterruption(CancellationToken observedCancellationToken)
    {
        return Volatile.Read(ref _routedSignalDelivered) != 0
            && !_competingCancellationToken.IsCancellationRequested
            && observedCancellationToken.CanBeCanceled
            && observedCancellationToken == _cancellation.Token
            && _cancellation.IsCancellationRequested;
    }

    /// <summary>
    /// Completes the acknowledgement as a confirmed provider interruption.
    /// </summary>
    public void ConfirmProviderInterruption()
    {
        Completion.TrySetResult(new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.ProviderInterruptionConfirmed, "The provider attempt observed the routed cancellation signal."));
    }

    /// <summary>
    /// Completes the acknowledgement without claiming that the provider observed the routed signal.
    /// </summary>
    public void CompleteWithoutConfirmedInterruption()
    {
        Completion.TrySetResult(CreateUnconfirmedResult());
    }

    /// <summary>
    /// Creates an unconfirmed result.
    /// </summary>
    /// <returns>The custom loop attempt cancellation result.</returns>
    public CustomLoopAttemptCancellationResult CreateUnconfirmedResult()
    {
        if (Volatile.Read(ref _routedSignalDelivered) != 0 && !_competingCancellationToken.IsCancellationRequested)
        {
            return new CustomLoopAttemptCancellationResult(CustomLoopAttemptCancellationStatus.SignalDelivered, "The cancellation signal was delivered, but the active operation completed or the acknowledgement window elapsed without confirmed provider interruption.");
        }

        var status = Volatile.Read(ref _signalRequested) == 0 ? CustomLoopAttemptCancellationStatus.NoActiveAttempt : CustomLoopAttemptCancellationStatus.OwnerUnavailable;
        var detail = status == CustomLoopAttemptCancellationStatus.NoActiveAttempt
            ? "The active operation completed before cancellation was routed."
            : _competingCancellationToken.IsCancellationRequested
                ? "A caller or deadline cancellation competed with the routed signal, so routed delivery could not be proved."
                : "The cancellation signal was requested, but delivery was not observed before the active operation completed or the acknowledgement window elapsed.";
        return new CustomLoopAttemptCancellationResult(status, detail);
    }

    /// <summary>
    /// Completes the acknowledgement because the workspace-host owner is no longer available.
    /// </summary>
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
            var callbackCompletion = _cancellation.CancelAsync();
            _ = callbackCompletion.ContinueWith(
                static completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (ObjectDisposedException)
        {
            Volatile.Write(ref _routedSignalDelivered, 0);
            CompleteWithoutConfirmedInterruption();
        }
    }
}
