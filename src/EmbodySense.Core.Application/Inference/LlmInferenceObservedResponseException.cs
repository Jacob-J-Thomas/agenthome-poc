using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Inference;

/// <summary>
/// Reports a successful terminal provider response whose required completion bookkeeping failed afterward.
/// </summary>
/// <remarks>
/// The response is conclusive provider evidence and must be checkpointed before the bookkeeping failure is surfaced.
/// Callers must not classify the attempt as outcome-unknown or redispatch it.
/// </remarks>
public sealed class LlmInferenceObservedResponseException : InvalidOperationException
{
    /// <summary>Initializes an observed response with its post-response failure.</summary>
    /// <param name="message">The bounded actionable failure detail.</param>
    /// <param name="response">The exact observed provider response.</param>
    /// <param name="innerException">The post-response bookkeeping failure.</param>
    public LlmInferenceObservedResponseException(string message, LlmInferenceResponse response, Exception innerException) : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(response);
        Response = response;
    }

    /// <summary>Gets the exact successful provider response that was already observed.</summary>
    public LlmInferenceResponse Response { get; }
}
