namespace EmbodySense.Core.Clients.Tests.CodexAppServer;

internal sealed class CodexRuntimeProbeTimeProvider : TimeProvider
{
    private readonly TimeSpan[] _timestamps;
    private readonly Action<int>? _onTimestampRead;
    private int _timestampReads;

    internal CodexRuntimeProbeTimeProvider(TimeSpan[] timestamps, Action<int>? onTimestampRead = null)
    {
        ArgumentNullException.ThrowIfNull(timestamps);
        if (timestamps.Length == 0)
        {
            throw new ArgumentException("At least one timestamp is required.", nameof(timestamps));
        }

        _timestamps = timestamps;
        _onTimestampRead = onTimestampRead;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        var read = Interlocked.Increment(ref _timestampReads);
        _onTimestampRead?.Invoke(read);
        return _timestamps[Math.Min(read - 1, _timestamps.Length - 1)].Ticks;
    }
}
