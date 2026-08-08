using EmbodySense.Core.Application.Capabilities.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Persists immutable verified artifacts and orthogonal activation state.</summary>
public interface ICapabilityArtifactStore : ICapabilityExecutableArtifactResolver
{
    /// <summary>Stages one verified immutable artifact.</summary>
    Task<CapabilityArtifactStoreResult> StageAsync(CapabilityArtifactStageRequest request, CancellationToken cancellationToken = default);

    /// <summary>Atomically activates one staged artifact using optimistic revision and idempotency checks.</summary>
    Task<CapabilityArtifactStoreResult> ActivateAsync(CapabilityArtifactActivationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Atomically restores the immediately prior proved activation.</summary>
    Task<CapabilityArtifactStoreResult> RollbackAsync(CapabilityId capabilityId, long expectedRevision, string operationId, CancellationToken cancellationToken = default);

    /// <summary>Reads the current proved activation for one capability.</summary>
    Task<CapabilityArtifactStoreResult> ReadAsync(CapabilityId capabilityId, CancellationToken cancellationToken = default);
}
