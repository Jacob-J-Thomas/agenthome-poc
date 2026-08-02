using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Tests.Support;

public sealed class TestCapabilityAdmissionService : ICapabilityAdmissionService
{
    public CapabilityAdmissionResult? AdmissionResult { get; init; }

    public CapabilityRevalidationResult? RevalidationResult { get; init; }

    public Exception? RevalidationException { get; init; }

    public Queue<CapabilityRevalidationResult> RevalidationResults { get; } = [];

    public Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (AdmissionResult is not null)
        {
            return Task.FromResult(AdmissionResult);
        }

        var snapshot = TestCapabilityAdmissionFactory.Create(requirements);
        var allowed = allowedCapabilityIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(snapshot.Pins.All(pin => allowed.Contains(pin.DescriptorIdentity.Id.Value))
            ? new CapabilityAdmissionResult(true, snapshot, "Test capability admission succeeded.")
            : new CapabilityAdmissionResult(false, null, "Test capability authority was narrower than the requirements."));
    }

    public Task<CapabilityRevalidationResult> RevalidateAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RevalidationException is not null)
        {
            throw RevalidationException;
        }
        if (RevalidationResults.TryDequeue(out var queued))
        {
            return Task.FromResult(queued);
        }

        if (RevalidationResult is not null)
        {
            return Task.FromResult(RevalidationResult);
        }

        var allowed = allowedCapabilityIds.Select(item => item.Value).ToHashSet(StringComparer.Ordinal);
        var valid = CapabilityAdmissionSnapshotValidator.Validate(snapshot) is null && snapshot.Pins.All(pin => allowed.Contains(pin.DescriptorIdentity.Id.Value));
        return Task.FromResult(new CapabilityRevalidationResult(valid, valid ? snapshot.Pins : [], valid ? "Test pins remain effective." : "Test pins failed revalidation."));
    }
}
