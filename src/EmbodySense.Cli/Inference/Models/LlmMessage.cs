namespace EmbodySense.Cli.Inference.Models;

internal sealed record LlmMessage
{
    public LlmMessage(LlmMessageRole role, string content)
    {
        if (!Enum.IsDefined(role) || role == LlmMessageRole.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(role),
                role,
                "Choose a concrete LLM message role.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        Role = role;
        Content = content;
    }

    public LlmMessageRole Role { get; }

    public string Content { get; }

    public static LlmMessage System(string content)
    {
        return new LlmMessage(LlmMessageRole.System, content);
    }

    public static LlmMessage User(string content)
    {
        return new LlmMessage(LlmMessageRole.User, content);
    }

    public static LlmMessage Assistant(string content)
    {
        return new LlmMessage(LlmMessageRole.Assistant, content);
    }
}
