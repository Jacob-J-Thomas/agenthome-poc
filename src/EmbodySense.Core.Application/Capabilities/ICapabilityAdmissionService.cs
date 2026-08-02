using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Resolves loop requirements at admission and revalidates immutable pins at every effect boundary.</summary>
public interface ICapabilityAdmissionService
{
    /// <summary>Resolves only the capabilities explicitly allowed by current loop and role authority.</summary>
    Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default);

    /// <summary>Recomputes current availability and narrower authority without rewriting historical evidence.</summary>
    Task<CapabilityRevalidationResult> RevalidateAsync(CapabilityAdmissionSnapshot snapshot, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default);
}
