namespace EmbodySense.Core.Application.Tests.Loops.Execution.Reconciliation;

internal sealed class GovernedLoopEffectReconciliationServiceTimeProvider(params DateTimeOffset[] values) : TimeProvider
{
    private readonly DateTimeOffset[] _values = values.Length == 0 ? throw new ArgumentException("At least one time value is required.", nameof(values)) : values;
    private int _index;

    public override DateTimeOffset GetUtcNow()
    {
        var index = Math.Min(Interlocked.Increment(ref _index) - 1, _values.Length - 1);
        return _values[index];
    }
}
