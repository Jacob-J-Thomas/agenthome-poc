using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Application.Tests.Capabilities;

internal sealed class StubCapabilityAuthorityLease : ICapabilityAuthorityLease
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
