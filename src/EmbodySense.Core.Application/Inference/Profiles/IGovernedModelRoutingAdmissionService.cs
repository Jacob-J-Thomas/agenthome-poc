using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Resolves exact model-profile routing evidence inside the canonical governed-loop admission fence.</summary>
public interface IGovernedModelRoutingAdmissionService
{
    /// <summary>Resolves a complete routing snapshot or structured fail-closed posture.</summary>
    Task<GovernedModelRoutingAdmissionResult> AdmitAsync(GovernedModelRoutingAdmissionRequest? request, CancellationToken cancellationToken = default);
}
