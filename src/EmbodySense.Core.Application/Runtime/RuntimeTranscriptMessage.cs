using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime;

public sealed record RuntimeTranscriptMessage(LlmMessageRole Role, string Content)
{
    public RuntimeTranscriptMessage(LlmMessage message)
        : this(message.Role, message.Content)
    {
    }
}
