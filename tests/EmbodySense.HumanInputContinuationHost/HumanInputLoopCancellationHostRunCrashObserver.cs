using EmbodySense.Core.Application.Loops.Models;

namespace EmbodySense.HumanInputContinuationHost;

/// <summary>Terminates the cancellation host after a named number of durable canonical run publications.</summary>
internal sealed class HumanInputLoopCancellationHostRunCrashObserver
{
    private readonly int _targetProvenOrdinal;
    private int _targetProvenCount;

    internal HumanInputLoopCancellationHostRunCrashObserver(string crashBoundary)
    {
        _targetProvenOrdinal = crashBoundary switch
        {
            "CheckpointRetiredCommitted" => 2,
            "FinalRunCancelledCommitted" => 3,
            _ => 0,
        };
    }

    internal ValueTask ObserveAsync(CustomLoopRunPublicationBoundary boundary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (boundary == CustomLoopRunPublicationBoundary.TargetProven
            && _targetProvenOrdinal != 0
            && Interlocked.Increment(ref _targetProvenCount) == _targetProvenOrdinal)
        {
            Environment.Exit(86);
        }

        return ValueTask.CompletedTask;
    }
}
