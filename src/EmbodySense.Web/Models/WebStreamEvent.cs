namespace EmbodySense.Web.Models;

public sealed record WebStreamEvent(
    string Type,
    string? Text = null,
    string? Surface = null,
    string? Model = null,
    string? Error = null)
{
    public static WebStreamEvent AssistantDelta(string text) => new("assistant_delta", Text: text);

    public static WebStreamEvent AssistantFinal(string text, string surface, string? model) => new("assistant_final", Text: text, Surface: surface, Model: model);

    public static WebStreamEvent Cancelled(string text) => new("cancelled", Text: text);

    public static WebStreamEvent Failure(string error) => new("error", Error: error);
}
