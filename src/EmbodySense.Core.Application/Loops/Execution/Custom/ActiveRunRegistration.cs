using System.Collections.Concurrent;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

internal sealed class ActiveRunRegistration(ConcurrentDictionary<string, byte> activeRuns, string runId) : IDisposable
{
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            activeRuns.TryRemove(runId, out _);
        }
    }
}
