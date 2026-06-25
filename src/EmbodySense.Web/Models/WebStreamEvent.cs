namespace EmbodySense.Web.Models;

public sealed record WebStreamEvent
{
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

    public string Type { get; }

    public string? Text { get; }

    public string? Error { get; }

    public IReadOnlyList<WebTranscriptMessage> Messages { get; }

    public static WebStreamEvent AssistantDelta(string text) => new("assistant_delta", text: text);

    public static WebStreamEvent AssistantFinal(string text) => new("assistant_final", text: text);

    public static WebStreamEvent System(string text) => new("system", text: text);

    public static WebStreamEvent VerboseContext(string text) => new("verbose_context", text: text);

    public static WebStreamEvent HistoryLoaded(IReadOnlyList<WebTranscriptMessage> messages) => new("history_loaded", messages: messages);

    public static WebStreamEvent Cancelled(string text) => new("cancelled", text: text);

    public static WebStreamEvent Failure(string error) => new("error", error: error);
}
