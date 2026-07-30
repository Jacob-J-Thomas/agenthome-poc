using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime;

/// <summary>
/// Represents a runtime context message.
/// </summary>
public sealed record RuntimeContextMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeContextMessage"/> type.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="source">The source.</param>
    /// <param name="detail">The detail.</param>
    public RuntimeContextMessage(LlmMessage message, RuntimeContextSource source, string detail)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Enum.IsDefined(source) || source == RuntimeContextSource.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Choose a concrete context source.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Message = message;
        Source = source;
        Detail = detail;
    }

    /// <summary>
    /// Gets the LLM message.
    /// </summary>
    /// <value>The LLM message.</value>
    public LlmMessage Message { get; }

    /// <summary>
    /// Gets the runtime context source.
    /// </summary>
    /// <value>The runtime context source.</value>
    public RuntimeContextSource Source { get; }

    /// <summary>
    /// Gets the detail.
    /// </summary>
    /// <value>The detail.</value>
    public string Detail { get; }
}
