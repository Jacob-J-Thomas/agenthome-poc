namespace EmbodySense.Core.Clients.Capabilities;

internal sealed class CapabilityProcessOutputBudget
{
    private readonly long _maximumBytes;
    private long _observedBytes;

    public CapabilityProcessOutputBudget(long maximumBytes) => _maximumBytes = maximumBytes;

    public void Account(int byteCount)
    {
        if (Interlocked.Add(ref _observedBytes, byteCount) > _maximumBytes)
        {
            throw new CapabilityProcessOutputLimitException();
        }
    }
}
