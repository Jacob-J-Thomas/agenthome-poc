using EmbodySense.Core.Common.Inference.Models;
namespace EmbodySense.Core.Common.Inference;

/// <summary>
/// Represents an LLM message.
/// </summary>
public sealed record LlmMessage
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LlmMessage"/> type.
    /// </summary>
    /// <param name="role">A concrete, non-unknown message role.</param>
    /// <param name="content">Non-empty message content.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="role"/> is unknown or outside the defined role set.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="content"/> is empty or whitespace.</exception>
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

    /// <summary>
    /// Gets the LLM message role.
    /// </summary>
    /// <value>The LLM message role.</value>
    public LlmMessageRole Role { get; }

    /// <summary>
    /// Gets the content.
    /// </summary>
    /// <value>The content.</value>
    public string Content { get; }

    /// <summary>
    /// Creates a system-role LLM message.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The LLM message.</returns>
    public static LlmMessage System(string content)
    {
        return new LlmMessage(LlmMessageRole.System, content);
    }

    /// <summary>
    /// Creates a user-role LLM message.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The LLM message.</returns>
    public static LlmMessage User(string content)
    {
        return new LlmMessage(LlmMessageRole.User, content);
    }

    /// <summary>
    /// Creates an assistant-role LLM message.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The LLM message.</returns>
    public static LlmMessage Assistant(string content)
    {
        return new LlmMessage(LlmMessageRole.Assistant, content);
    }

    /// <summary>
    /// Creates a tool-role LLM message.
    /// </summary>
    /// <param name="content">The content.</param>
    /// <returns>The LLM message.</returns>
    public static LlmMessage Tool(string content)
    {
        return new LlmMessage(LlmMessageRole.Tool, content);
    }
}
