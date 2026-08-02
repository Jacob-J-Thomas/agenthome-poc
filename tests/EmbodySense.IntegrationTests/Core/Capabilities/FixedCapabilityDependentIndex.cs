using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class FixedCapabilityDependentIndex : ICapabilityDependentIndex
{
    private readonly TaskCompletionSource _captureEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CapabilityDependentIndexSnapshot _snapshot = new(CapabilityDependentIndexStatus.Available, new string('a', 64), [], "available");

    internal int CaptureCount { get; private set; }
    internal Task CaptureEntered => _captureEntered.Task;

    public Task<CapabilityDependentIndexSnapshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaptureCount++;
        _captureEntered.TrySetResult();
        return Task.FromResult(_snapshot);
    }
}
