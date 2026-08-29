using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.HumanInputContinuationHost;

internal sealed class HumanInputResponseContinuationHostCapabilityAdmissionService : ICapabilityAdmissionService
{
    public Task<CapabilityAdmissionResult> AdmitAsync(
        CapabilityDependencyManifest requirements,
        IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(allowedCapabilityIds);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CapabilityAdmissionResult(false, null, "The external continuation fixture only revalidates its exact seeded admission evidence."));
    }

    public Task<CapabilityRevalidationResult> RevalidateAsync(
        CapabilityAdmissionSnapshot snapshot,
        IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(allowedCapabilityIds);
        cancellationToken.ThrowIfCancellationRequested();
        var allowed = allowedCapabilityIds.Select(value => value.Value).ToHashSet(StringComparer.Ordinal);
        var valid = CapabilityAdmissionSnapshotValidator.Validate(snapshot) is null
            && snapshot.Pins.All(pin => allowed.Contains(pin.DescriptorIdentity.Id.Value));
        return Task.FromResult(new CapabilityRevalidationResult(
            valid,
            valid ? snapshot.Pins : [],
            valid ? "The exact seeded capability admission remains inside the bounded Exit authority." : "The seeded capability admission is malformed or outside the bounded Exit authority.",
            valid ? CapabilityRevalidationStatus.Active : CapabilityRevalidationStatus.AuthorityNarrowed));
    }
}
