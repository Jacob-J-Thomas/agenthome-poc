namespace EmbodySense.Core.Application.Tests.Loops.Admission;

internal sealed class GovernedLoopAdmissionTestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
