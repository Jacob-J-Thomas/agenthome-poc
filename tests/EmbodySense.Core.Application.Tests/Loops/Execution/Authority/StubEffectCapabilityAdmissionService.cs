using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Capabilities.Models;

namespace EmbodySense.Core.Application.Tests.Loops.Execution.Authority;

internal sealed class StubEffectCapabilityAdmissionService : ICapabilityAdmissionService
{
    internal CapabilityRevalidationResult Result { get; set; } = null!;

    internal int Calls { get; private set; }

    public Task<CapabilityAdmissionResult> AdmitAsync(CapabilityDependencyManifest requirements, IReadOnlyCollection<CapabilityId> allowedCapabilityIds, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CapabilityRevalidationResult> RevalidateAsync(
        CapabilityAdmissionSnapshot snapshot,
        IReadOnlyCollection<CapabilityId> allowedCapabilityIds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Result);
    }
}
