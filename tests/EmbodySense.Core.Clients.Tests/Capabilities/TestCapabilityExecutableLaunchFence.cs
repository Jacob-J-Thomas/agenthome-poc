using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.Core.Clients.Tests.Capabilities;

internal sealed class TestCapabilityExecutableLaunchFence : ICapabilityExecutableLaunchFence
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
