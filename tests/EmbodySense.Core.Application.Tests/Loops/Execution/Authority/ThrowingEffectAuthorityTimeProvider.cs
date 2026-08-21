namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class ThrowingEffectAuthorityTimeProvider : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => throw new InvalidOperationException("trusted-time-unavailable");
}
