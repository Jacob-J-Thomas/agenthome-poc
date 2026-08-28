using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.HumanReview;

internal sealed class RecordingHumanReviewCapabilityAdmissionService : ICapabilityAdmissionService
{
    public int RevalidateCount { get; private set; }

    public Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CapabilityRevalidationResult> RevalidateAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
    {
        RevalidateCount++;
        throw new NotSupportedException();
    }
}
