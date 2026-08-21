using System.Collections.Concurrent;

namespace EmbodySense.Core.Persistence.Tests.Loops;

internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();
    private readonly TaskCompletionSource<object?> _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        _work.Enqueue((callback, state));
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
}
