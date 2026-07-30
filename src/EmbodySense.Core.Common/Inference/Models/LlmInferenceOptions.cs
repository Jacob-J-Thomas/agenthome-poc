namespace EmbodySense.Core.Common.Inference.Models;

/// <summary>
/// Defines optional model-generation parameters for one inference request.
/// </summary>
public sealed record LlmInferenceOptions
{
    /// <summary>
    /// Gets an option set that leaves all generation parameters provider-defined.
    /// </summary>
    /// <value>The default LLM inference options.</value>
    public static LlmInferenceOptions Default { get; } = new();

    /// <summary>
    /// Gets the optional sampling temperature forwarded to the inference adapter.
    /// </summary>
    /// <value>The temperature.</value>
    public decimal? Temperature { get; init; }

    /// <summary>
    /// Gets the optional maximum number of output tokens requested from the provider.
    /// </summary>
    /// <value>The maximum output token count.</value>
    public int? MaxOutputTokenCount { get; init; }
}
