using EmbodySense.Core.Persistence.HumanInput.Requests.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputRequestPublicationHostCrashObserver
{
    private readonly string _boundary;
    private readonly int _ordinal;
    private int _observed;

    internal HumanInputRequestPublicationHostCrashObserver(string boundary, int ordinal)
    {
        if ((boundary != "none" && !Enum.TryParse<HumanInputRequestPersistenceBoundary>(boundary, ignoreCase: false, out _)) || ordinal < 1)
        {
            throw new ArgumentException("The requested Human Input request persistence crash observer is invalid.", nameof(boundary));
        }

        _boundary = boundary;
        _ordinal = ordinal;
    }

    internal ValueTask ObserveAsync(HumanInputRequestPersistenceBoundary boundary, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_boundary != "none"
            && string.Equals(_boundary, boundary.ToString(), StringComparison.Ordinal)
            && Interlocked.Increment(ref _observed) == _ordinal)
        {
            Environment.Exit(86);
        }

        return ValueTask.CompletedTask;
    }
}
