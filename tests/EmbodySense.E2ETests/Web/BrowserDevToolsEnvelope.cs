using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class BrowserDevToolsEnvelope
{
    public static JsonDocument Parse(string payload)
    {
        return JsonDocument.Parse(payload);
    }

    public static bool TryReadCommandId(JsonElement envelope, out int commandId)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-envelope", "unknown", null, "envelope-not-object");
        }

        if (envelope.TryGetProperty("id", out var id))
        {
            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out commandId))
            {
                throw new BrowserDevToolsException("malformed-envelope", "unknown", null, "command-id-invalid");
            }

            return true;
        }

        commandId = default;
        if (envelope.TryGetProperty("result", out _) || envelope.TryGetProperty("error", out _))
        {
            throw new BrowserDevToolsException("malformed-envelope", "unknown", null, "response-id-missing");
        }

        if (envelope.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            return false;
        }

        throw new BrowserDevToolsException("malformed-envelope", "unknown", null, "event-method-missing");
    }
}
