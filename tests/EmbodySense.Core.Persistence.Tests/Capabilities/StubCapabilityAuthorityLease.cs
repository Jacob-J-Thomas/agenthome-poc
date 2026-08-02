using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Persistence.Tests.Capabilities;

internal sealed class StubCapabilityAuthorityLease : ICapabilityAuthorityLease
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
