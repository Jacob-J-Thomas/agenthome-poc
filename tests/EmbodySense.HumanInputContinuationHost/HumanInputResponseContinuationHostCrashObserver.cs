using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Persistence.Loops.Execution.Sleep.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostCrashObserver
{
    private readonly int _ordinal;
    private readonly string _plane;
    private readonly string _boundary;
    private int _observed;

    private HumanInputResponseContinuationHostCrashObserver(string plane, string boundary, int ordinal)
    {
        _plane = plane;
        _boundary = boundary;
        _ordinal = ordinal;
    }

    internal static HumanInputResponseContinuationHostCrashObserver Create(string plane, string boundary, int ordinal)
    {
        if (plane is not ("none" or "run" or "sleep") || string.IsNullOrWhiteSpace(boundary))
        {
            throw new ArgumentException("The requested crash observer is invalid.", nameof(plane));
        }

        return new HumanInputResponseContinuationHostCrashObserver(plane, boundary, ordinal);
    }

    internal ValueTask ObserveRunAsync(CustomLoopRunPublicationBoundary boundary, CancellationToken cancellationToken)
    {
        Observe("run", boundary.ToString());
        return ValueTask.CompletedTask;
    }

    internal void ObserveSleep(GovernedLoopSleepStorePersistenceBoundary boundary)
        => Observe("sleep", boundary.ToString());

    private void Observe(string plane, string boundary)
    {
        if (!string.Equals(_plane, plane, StringComparison.Ordinal)
            || !string.Equals(_boundary, boundary, StringComparison.Ordinal)
            || Interlocked.Increment(ref _observed) != _ordinal)
        {
            return;
        }

        Environment.Exit(86);
    }
}
