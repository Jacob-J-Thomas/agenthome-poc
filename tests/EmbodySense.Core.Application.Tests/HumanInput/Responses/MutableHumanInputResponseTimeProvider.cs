namespace EmbodySense.Core.Application.Tests.HumanInput.Responses;

internal sealed class MutableHumanInputResponseTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
