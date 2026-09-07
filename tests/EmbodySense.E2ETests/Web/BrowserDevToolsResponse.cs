using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class BrowserDevToolsResponse
{
    private const int MaxMessageLength = 160;

    public static void Validate(string method, JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-envelope", method, null, "response-not-object");
        }

        if (envelope.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("protocol-error", method, ReadCode(error), ReadMessage(error));
        }

        if (!envelope.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-envelope", method, null, "result-missing");
        }

        if (string.Equals(method, "Runtime.evaluate", StringComparison.Ordinal)
            && result.TryGetProperty("exceptionDetails", out var exceptionDetails)
            && exceptionDetails.ValueKind == JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("runtime-exception", method, null, ReadMessage(exceptionDetails));
        }
    }

    private static int? ReadCode(JsonElement error)
    {
        return error.TryGetProperty("code", out var code) && code.TryGetInt32(out var value) ? value : null;
    }

    private static string ReadMessage(JsonElement source)
    {
        var value = source.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
            ? message.GetString()
            : source.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : "unavailable";
        value ??= "unavailable";
        value = value.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= MaxMessageLength ? value : value[..MaxMessageLength];
    }
}
