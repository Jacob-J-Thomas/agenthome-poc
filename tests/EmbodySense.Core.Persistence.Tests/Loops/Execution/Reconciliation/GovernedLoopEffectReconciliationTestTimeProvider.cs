namespace EmbodySense.Core.Persistence.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationTestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}
