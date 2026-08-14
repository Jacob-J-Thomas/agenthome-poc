namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class FixedEffectAuthorityTimeProvider(DateTimeOffset value) : TimeProvider
{
    internal DateTimeOffset Value { get; set; } = value;

    public override DateTimeOffset GetUtcNow() => Value;
}
