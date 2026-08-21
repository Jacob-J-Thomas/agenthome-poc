using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Classifies the exact current attempt input without returning raw values to routing policy.</summary>
public interface IModelInferenceDataPostureSource
{
    /// <summary>Classifies the exact provider payload for one run, node, and activation without trusting authored labels.</summary>
    Task<ModelInferenceDataPosture> ReadAsync(ModelInferenceDataPostureRequest request, CancellationToken cancellationToken = default);
}
