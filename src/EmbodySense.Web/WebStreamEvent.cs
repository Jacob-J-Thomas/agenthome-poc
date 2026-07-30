using EmbodySense.Web.Models;
namespace EmbodySense.Web;

/// <summary>
/// Represents one typed server-to-browser default-conversation stream event.
/// </summary>
public sealed record WebStreamEvent
{
    /// <summary>
    /// Initializes a stream event.
    /// </summary>
    /// <param name="type">The stable event discriminator consumed by the browser.</param>
    /// <param name="text">Optional assistant, system, context, or cancellation text.</param>
    /// <param name="error">Optional bounded failure text.</param>
    /// <param name="messages">Optional complete transcript replacement; omitted values become an empty list.</param>
    public WebStreamEvent(
        string type,
        string? text = null,
        string? error = null,
        IReadOnlyList<WebTranscriptMessage>? messages = null)
    {
        Type = type;
        Text = text;
        Error = error;
        Messages = messages ?? [];
    }

    /// <summary>
    /// Gets the stable browser event discriminator.
    /// </summary>
    public string Type { get; }

    /// <summary>
    /// Gets assistant, system, context, or cancellation text when applicable.
    /// </summary>
    public string? Text { get; }

    /// <summary>
    /// Gets bounded failure text for an error event.
    /// </summary>
    public string? Error { get; }

    /// <summary>
    /// Gets the complete transcript replacement for a history-loaded event.
    /// </summary>
    public IReadOnlyList<WebTranscriptMessage> Messages { get; }

    /// <summary>
    /// Creates a streamed assistant delta.
    /// </summary>
    /// <param name="text">The nonempty incremental assistant text.</param>
    /// <returns>An <c>assistant_delta</c> event.</returns>
    public static WebStreamEvent AssistantDelta(string text) => new("assistant_delta", text: text);

    /// <summary>
    /// Creates a final assistant or command-output message.
    /// </summary>
    /// <param name="text">The complete final text.</param>
    /// <returns>An <c>assistant_final</c> event.</returns>
    public static WebStreamEvent AssistantFinal(string text) => new("assistant_final", text: text);

    /// <summary>
    /// Creates a runtime system-status message.
    /// </summary>
    /// <param name="text">The complete system text.</param>
    /// <returns>A <c>system</c> event.</returns>
    public static WebStreamEvent System(string text) => new("system", text: text);

    /// <summary>
    /// Creates a verbose startup-context projection.
    /// </summary>
    /// <param name="text">The projected context text.</param>
    /// <returns>A <c>verbose_context</c> event.</returns>
    public static WebStreamEvent VerboseContext(string text) => new("verbose_context", text: text);

    /// <summary>
    /// Creates a canonical transcript replacement.
    /// </summary>
    /// <param name="messages">The complete ordered transcript.</param>
    /// <returns>A <c>history_loaded</c> event.</returns>
    public static WebStreamEvent HistoryLoaded(IReadOnlyList<WebTranscriptMessage> messages) => new("history_loaded", messages: messages);

    /// <summary>
    /// Creates a bounded turn-cancellation event.
    /// </summary>
    /// <param name="text">The cancellation explanation.</param>
    /// <returns>A <c>cancelled</c> event.</returns>
    public static WebStreamEvent Cancelled(string text) => new("cancelled", text: text);

    /// <summary>
    /// Creates a bounded runtime failure event.
    /// </summary>
    /// <param name="error">The client-safe failure explanation.</param>
    /// <returns>An <c>error</c> event.</returns>
    public static WebStreamEvent Failure(string error) => new("error", error: error);
}
