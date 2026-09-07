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

    public static bool TryReadConsumedEventParameters(string method, JsonElement envelope, out JsonElement parameters)
    {
        if (!IsConsumedEvent(method))
        {
            parameters = default;
            return false;
        }

        if (!envelope.TryGetProperty("params", out parameters) || parameters.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-event", method, null, "event-params-invalid");
        }

        return true;
    }

    public static JsonElement RequireObjectProperty(JsonElement source, string method, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-event", method, null, "nested-object-invalid");
        }

        return value;
    }

    public static string RequireStringProperty(JsonElement source, string method, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BrowserDevToolsException("malformed-event", method, null, "nested-string-invalid");
        }

        return value.GetString()!;
    }

    public static string? ReadOptionalStringProperty(JsonElement source, string method, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new BrowserDevToolsException("malformed-event", method, null, "nested-string-invalid");
        }

        return value.GetString();
    }

    public static bool ReadOptionalBooleanProperty(JsonElement source, string method, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new BrowserDevToolsException("malformed-event", method, null, "nested-boolean-invalid");
        }

        return value.ValueKind == JsonValueKind.True;
    }

    private static bool IsConsumedEvent(string method)
    {
        return method is "Network.loadingFailed"
            or "Network.requestWillBeSent"
            or "Network.webSocketCreated"
            or "Network.loadingFinished"
            or "Network.webSocketClosed"
            or "Network.responseReceived"
            or "Page.javascriptDialogOpening"
            or "Page.frameNavigated"
            or "Runtime.exceptionThrown"
            or "Runtime.consoleAPICalled"
            or "Log.entryAdded";
    }
}
