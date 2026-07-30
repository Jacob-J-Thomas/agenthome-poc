using System.Collections.Concurrent;

namespace EmbodySense.Core.Application.Loops.Execution.Custom;

/// <summary>
/// Represents an active run registration.
/// </summary>
/// <param name="activeRuns">The active runs.</param>
/// <param name="runId">The run ID.</param>
internal sealed class ActiveRunRegistration(ConcurrentDictionary<string, byte> activeRuns, string runId) : IDisposable
{
    private int _disposed;

    /// <summary>
    /// Executes the dispose operation.
    /// </summary>
    /// <returns>The operation.</returns>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            activeRuns.TryRemove(runId, out _);
        }
    }
}
