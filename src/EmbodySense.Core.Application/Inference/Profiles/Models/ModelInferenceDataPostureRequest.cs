using EmbodySense.Core.Common.Inference;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests server-owned classification of the exact provider input without persisting raw content.</summary>
public sealed record ModelInferenceDataPostureRequest(
    GovernedModelAttemptAdmissionRequest Attempt,
    LlmInferenceRequest InferenceRequest,
    string InputPayloadHash);
