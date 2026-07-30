using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime;

/// <summary>
/// Represents a runtime transcript message.
/// </summary>
/// <param name="Role">The model message role assigned to the content.</param>
/// <param name="Content">The exact content.</param>
public sealed record RuntimeTranscriptMessage(LlmMessageRole Role, string Content)
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeTranscriptMessage"/> type.
    /// </summary>
    /// <param name="message">The message.</param>
    public RuntimeTranscriptMessage(LlmMessage message)
        : this(message.Role, message.Content)
    {
    }
}
