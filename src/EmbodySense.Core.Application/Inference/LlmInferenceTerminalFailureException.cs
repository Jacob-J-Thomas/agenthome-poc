namespace EmbodySense.Core.Application.Inference;

/// <summary>
/// Reports a conclusive terminal provider failure observed after request dispatch.
/// </summary>
/// <remarks>
/// This exception distinguishes a provider-declared terminal failure from a transport failure whose external
/// outcome remains unknown. Callers may durably close the attempt without quarantining or redispatching it.
/// </remarks>
public sealed class LlmInferenceTerminalFailureException : InvalidOperationException
{
    /// <summary>
    /// Initializes a conclusive terminal provider failure.
    /// </summary>
    /// <param name="message">The actionable provider failure detail.</param>
    /// <param name="providerResponseId">The optional stable provider turn or response identity.</param>
    /// <param name="innerException">Optional retained evidence about a failure observed after the provider outcome.</param>
    public LlmInferenceTerminalFailureException(string message, string? providerResponseId = null, Exception? innerException = null) : base(message, innerException)
    {
        ProviderResponseId = string.IsNullOrWhiteSpace(providerResponseId) ? null : providerResponseId.Trim();
    }

    /// <summary>Gets the optional stable provider turn or response identity.</summary>
    public string? ProviderResponseId { get; }
}
