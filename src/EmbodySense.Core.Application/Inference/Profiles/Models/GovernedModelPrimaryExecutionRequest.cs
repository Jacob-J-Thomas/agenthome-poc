using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Governance.Tools;

namespace EmbodySense.Core.Application.Inference.Profiles.Models;

/// <summary>Requests exact admitted-primary dispatch after durable usage reservation.</summary>
public sealed record GovernedModelPrimaryExecutionRequest(
    GovernedModelAttemptAdmissionRequest Admission,
    LlmInferenceRequest InferenceRequest,
    IToolBroker? ToolBroker = null);
