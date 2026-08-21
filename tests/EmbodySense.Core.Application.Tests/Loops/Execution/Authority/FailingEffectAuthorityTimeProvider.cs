namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class FailingEffectAuthorityTimeProvider(int validReads) : TimeProvider
{
    private int _reads;

    public override DateTimeOffset GetUtcNow()
        => Interlocked.Increment(ref _reads) <= validReads
            ? GovernedLoopEffectAuthorityTestFixture.Now.AddTicks(_reads)
            : throw new InvalidOperationException("trusted-time-unavailable");
}
