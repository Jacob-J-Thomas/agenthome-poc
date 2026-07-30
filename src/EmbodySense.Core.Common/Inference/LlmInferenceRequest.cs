using EmbodySense.Core.Common.Inference.Models;
namespace EmbodySense.Core.Common.Inference;

/// <summary>
/// Represents an LLM inference request.
/// </summary>
public sealed record LlmInferenceRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="LlmInferenceRequest"/> type.
    /// </summary>
    /// <param name="messages">The messages.</param>
    /// <param name="options">The options.</param>
    /// <param name="instructionContext">The instruction context.</param>
    public LlmInferenceRequest(
        IReadOnlyList<LlmMessage> messages,
        LlmInferenceOptions? options = null,
        LlmInferenceInstructionContext? instructionContext = null)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            throw new ArgumentException(
                "At least one message is required for LLM inferencing.",
                nameof(messages));
        }

        Messages = messages.ToArray();
        Options = options ?? LlmInferenceOptions.Default;
        InstructionContext = instructionContext;
    }

    /// <summary>
    /// Gets the LLM messages.
    /// </summary>
    /// <value>The LLM messages.</value>
    public IReadOnlyList<LlmMessage> Messages { get; }

    /// <summary>
    /// Gets the LLM inference options.
    /// </summary>
    /// <value>The LLM inference options.</value>
    public LlmInferenceOptions Options { get; }

    /// <summary>
    /// Gets the LLM inference instruction context.
    /// </summary>
    /// <value>The LLM inference instruction context.</value>
    public LlmInferenceInstructionContext? InstructionContext { get; }

    /// <summary>
    /// Creates an LLM inference request from user text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="options">The options.</param>
    /// <returns>The LLM inference request.</returns>
    public static LlmInferenceRequest FromUserText(string text, LlmInferenceOptions? options = null)
    {
        return new LlmInferenceRequest([LlmMessage.User(text)], options);
    }
}
