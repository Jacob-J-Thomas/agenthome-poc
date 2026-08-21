namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

internal sealed class FixedAuthorityDelegationTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = utcNow;

    internal bool Throw { get; set; }

    public override DateTimeOffset GetUtcNow()
        => Throw ? throw new InvalidOperationException("Injected trusted-time failure.") : UtcNow;
}
