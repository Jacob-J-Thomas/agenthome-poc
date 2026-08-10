namespace EmbodySense.Core.Application.Tests.HumanInput.Lifecycle;

internal sealed class RecordingHumanInputTimeProvider(DateTimeOffset value) : TimeProvider
{
    internal int Calls { get; private set; }

    internal bool ThrowOnRead { get; set; }

    internal DateTimeOffset Value { get; set; } = value;

    public override DateTimeOffset GetUtcNow()
    {
        Calls++;
        return ThrowOnRead ? throw new InvalidOperationException("Clock must not be consulted.") : Value;
    }
}
