using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.Models;

public sealed record RuntimeContextMessage
{
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

    public LlmMessage Message { get; }

    public RuntimeContextSource Source { get; }

    public string Detail { get; }
}
