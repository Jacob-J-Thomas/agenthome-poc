using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Runtime.Commands.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Runtime.Commands;

/// <summary>
/// Represents a runtime command result.
/// </summary>
public sealed record RuntimeCommandResult
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeCommandResult"/> type.
    /// </summary>
    /// <param name="handled">The handled.</param>
    /// <param name="output">The output.</param>
    /// <param name="prompt">The prompt.</param>
    /// <param name="awaitingInput">The awaiting input.</param>
    /// <param name="exitRequested">The exit requested.</param>
    /// <param name="restoredMessages">The restored messages.</param>
    /// <param name="replaceTranscript">The replace transcript.</param>
    public RuntimeCommandResult(
        bool handled,
        string output = "",
        string? prompt = null,
        bool awaitingInput = false,
        bool exitRequested = false,
        IReadOnlyList<LlmMessage>? restoredMessages = null,
        bool replaceTranscript = false)
    {
        Handled = handled;
        Output = output;
        Prompt = prompt;
        AwaitingInput = awaitingInput;
        ExitRequested = exitRequested;
        RestoredMessages = restoredMessages ?? [];
        ReplaceTranscript = replaceTranscript;
    }

    /// <summary>
    /// Gets a value indicating whether the handled condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the handled condition holds; otherwise, <see langword="false"/>.</value>
    public bool Handled { get; }

    /// <summary>
    /// Gets the output.
    /// </summary>
    /// <value>The output.</value>
    public string Output { get; }

    /// <summary>
    /// Gets the prompt.
    /// </summary>
    /// <value>The prompt.</value>
    public string? Prompt { get; }

    /// <summary>
    /// Gets a value indicating whether the awaiting input condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the awaiting input condition holds; otherwise, <see langword="false"/>.</value>
    public bool AwaitingInput { get; }

    /// <summary>
    /// Gets a value indicating whether the exit requested condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the exit requested condition holds; otherwise, <see langword="false"/>.</value>
    public bool ExitRequested { get; }

    /// <summary>
    /// Gets the restored messages LLM messages.
    /// </summary>
    /// <value>The restored messages LLM messages.</value>
    public IReadOnlyList<LlmMessage> RestoredMessages { get; }

    /// <summary>
    /// Gets a value indicating whether the replace transcript condition holds.
    /// </summary>
    /// <value><see langword="true"/> when the replace transcript condition holds; otherwise, <see langword="false"/>.</value>
    public bool ReplaceTranscript { get; }

    /// <summary>
    /// Gets the not handled runtime command result.
    /// </summary>
    /// <value>The not handled runtime command result.</value>
    public static RuntimeCommandResult NotHandled { get; } = new(false);

    /// <summary>
    /// Creates a runtime command result representing handled output.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <param name="restoredMessages">The restored messages.</param>
    /// <returns>The runtime command result.</returns>
    public static RuntimeCommandResult HandledOutput(string output, IReadOnlyList<LlmMessage>? restoredMessages = null)
    {
        return new RuntimeCommandResult(true, output, restoredMessages: restoredMessages, replaceTranscript: restoredMessages is not null);
    }

    /// <summary>
    /// Creates a runtime command result representing handled prompt.
    /// </summary>
    /// <param name="output">The output.</param>
    /// <param name="prompt">The prompt.</param>
    /// <returns>The runtime command result.</returns>
    public static RuntimeCommandResult HandledPrompt(string output, string prompt)
    {
        return new RuntimeCommandResult(true, output, prompt, awaitingInput: true);
    }

    /// <summary>
    /// Creates a runtime command result representing handled exit.
    /// </summary>
    /// <returns>The runtime command result.</returns>
    public static RuntimeCommandResult HandledExit()
    {
        return new RuntimeCommandResult(true, exitRequested: true);
    }
}
