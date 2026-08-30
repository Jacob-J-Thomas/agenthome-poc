using EmbodySense.Core.Application.Capabilities;

namespace EmbodySense.CancellationHost.Persistence;

internal sealed class HumanReviewOrderedReleaseProcessAuthorityLease : ICapabilityAuthorityLease
{
    internal static HumanReviewOrderedReleaseProcessAuthorityLease Instance { get; } = new();

    private HumanReviewOrderedReleaseProcessAuthorityLease()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
