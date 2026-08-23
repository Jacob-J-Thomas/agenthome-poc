using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Adapts the current exact generic/delegated authority owner into model attempt eligibility.</summary>
public interface IModelAttemptAuthorityRevalidator
{
    /// <summary>Revalidates current authority for the exact admitted primary and node activation.</summary>
    Task<ModelAttemptAuthorityEvidence> RevalidateAsync(GovernedModelAttemptAdmissionRequest request, GovernedModelRoutingAdmissionEntry node, GovernedModelProfilePin primary, ModelInferenceDataPosture dataPosture, CancellationToken cancellationToken = default);
}
