namespace EmbodySense.CancellationHost.Persistence;

internal sealed class FixedAuthorityGrantTimeProvider(DateTimeOffset timestamp) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => timestamp;
}
