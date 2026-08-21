using System.Collections.Concurrent;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;

namespace EmbodySense.Core.Startup.Tests.Loops.Execution.Sleep;

internal sealed class ScriptedLocalWorkRunner : IGovernedLoopLocalWorkRunner
{
    private readonly ConcurrentQueue<GovernedLoopLocalWorkFamily> _calls = new();
    private int _callCount;

    internal Func<GovernedLoopLocalWorkFamily, CancellationToken, Task<GovernedLoopLocalWorkResult?>> Handler { get; set; }
        = static (_, _) => Task.FromResult<GovernedLoopLocalWorkResult?>(
            new GovernedLoopLocalWorkResult(GovernedLoopLocalWorkResultStatus.Empty, "no-work"));

    internal int CallCount => Volatile.Read(ref _callCount);

    internal IReadOnlyList<GovernedLoopLocalWorkFamily> Calls => _calls.ToArray();

    public Task<GovernedLoopLocalWorkResult?> RunOnceAsync(
        GovernedLoopLocalWorkFamily family,
        CancellationToken cancellationToken = default)
    {
        _calls.Enqueue(family);
        Interlocked.Increment(ref _callCount);
        return Handler(family, cancellationToken);
    }
}
