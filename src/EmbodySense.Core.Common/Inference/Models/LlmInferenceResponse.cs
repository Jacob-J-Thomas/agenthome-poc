namespace EmbodySense.Core.Common.Inference.Models;

using EmbodySense.Core.Common.Inference.Profiles.Models;

/// <summary>
/// Represents an LLM inference response.
/// </summary>
/// <param name="OutputText">The output text.</param>
/// <param name="Surface">The normalized owning runtime surface.</param>
/// <param name="Usage">Explicit authoritative or unavailable provider-usage evidence.</param>
/// <param name="Model">The model.</param>
/// <param name="ProviderResponseId">The provider response ID.</param>
/// <param name="ProviderId">The exact public provider identity.</param>
public sealed record LlmInferenceResponse(
    string OutputText,
    LlmInferenceSurface Surface,
    LlmInferenceUsageEvidence Usage,
    string? Model = null,
    string? ProviderResponseId = null,
    string? ProviderId = null)
{
    /// <summary>Gets required explicit authoritative or unavailable provider-usage evidence.</summary>
    public LlmInferenceUsageEvidence Usage { get; } = Usage ?? throw new ArgumentNullException(nameof(Usage));
}
