using System.Collections.Concurrent;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution;

internal sealed class QueuedSynchronizationContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _work = new();

    internal int PendingCallbacks => _work.Count;

    public override void Post(SendOrPostCallback callback, object? state)
        => _work.Enqueue((callback, state));

    internal void Drain()
    {
        var previousContext = Current;
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
            SetSynchronizationContext(previousContext);
        }
    }
}
