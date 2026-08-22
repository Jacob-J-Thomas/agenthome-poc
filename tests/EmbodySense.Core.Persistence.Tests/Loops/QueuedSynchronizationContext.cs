using System.Collections.Concurrent;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
    private readonly TaskCompletionSource<object?> _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _workSignal = new(0);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        _work.Enqueue((callback, state));
        _workSignal.Release();
        _firstPost.TrySetResult(null);
    }

    public Task WaitForPostAsync(TimeSpan timeout) => _firstPost.Task.WaitAsync(timeout);

    public void Drain()
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            while (_work.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }

    public async Task DrainUntilCompletedAsync(Task task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource(timeout);
        var previous = Current;
        try
        {
            while (!task.IsCompleted)
            {
                if (_work.TryDequeue(out var item))
                {
                    SetSynchronizationContext(this);
                    try
                    {
                        item.Callback(item.State);
                    }
                    finally
                    {
                        SetSynchronizationContext(previous);
                    }

                    continue;
                }

                await _workSignal.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }

            Drain();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            throw new TimeoutException($"The queued synchronization context did not drain the task within {timeout}.");
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
