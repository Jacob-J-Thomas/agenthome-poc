namespace EmbodySense.Core.Application.Tests.Governance.Authority.Delegation;

internal sealed class InlineSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state)
        => callback(state);
}
