using System.Text.Json;

namespace EmbodySense.E2ETests.Web;

internal static class BrowserDevToolsResponse
{
    public static void Validate(string method, JsonElement envelope)
    {
        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-envelope", method, null, "response-not-object");
        }

        var hasResult = envelope.TryGetProperty("result", out var result);
        if (envelope.TryGetProperty("error", out var error))
        {
            if (error.ValueKind != JsonValueKind.Object)
            {
                throw new BrowserDevToolsException("malformed-envelope", method, null, "error-not-object");
            }

            if (hasResult)
            {
                throw new BrowserDevToolsException("malformed-envelope", method, null, "result-error-conflict");
            }

            var code = ReadCode(error);
            var message = ReadString(error, "message");
            if (code is null || message is null)
            {
                throw new BrowserDevToolsException("malformed-envelope", method, null, "error-shape-invalid");
            }

            throw new BrowserDevToolsException("protocol-error", method, code, ReadTurnoverMessage(message));
        }

        if (!hasResult || result.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-envelope", method, null, "result-missing");
        }

        if (string.Equals(method, "Runtime.evaluate", StringComparison.Ordinal)
            && result.TryGetProperty("exceptionDetails", out var exceptionDetails))
        {
            if (exceptionDetails.ValueKind != JsonValueKind.Object)
            {
                throw new BrowserDevToolsException("malformed-envelope", method, null, "exception-details-invalid");
            }

            throw new BrowserDevToolsException("runtime-exception", method, null, "runtime-exception");
        }
    }

    public static JsonElement ReadRuntimeEvaluationValue(JsonElement envelope)
    {
        Validate("Runtime.evaluate", envelope);
        if (!envelope.GetProperty("result").TryGetProperty("result", out var remoteObject)
            || remoteObject.ValueKind != JsonValueKind.Object)
        {
            throw new BrowserDevToolsException("malformed-runtime-result", "Runtime.evaluate", null, "remote-result-missing");
        }

        var type = ReadString(remoteObject, "type");
        if (type is null)
        {
            throw new BrowserDevToolsException("malformed-runtime-result", "Runtime.evaluate", null, "remote-type-missing");
        }

        if (string.Equals(type, "undefined", StringComparison.Ordinal))
        {
            if (remoteObject.TryGetProperty("value", out _))
            {
                throw new BrowserDevToolsException("malformed-runtime-result", "Runtime.evaluate", null, "undefined-value-present");
            }

            return default;
        }

        if (!remoteObject.TryGetProperty("value", out var value) || !IsValidRemoteValue(type, value))
        {
            throw new BrowserDevToolsException("malformed-runtime-result", "Runtime.evaluate", null, "remote-value-invalid");
        }

        return value.Clone();
    }

    public static bool IsTrueRuntimeEvaluation(JsonElement envelope)
    {
        return ReadRuntimeEvaluationValue(envelope).ValueKind == JsonValueKind.True;
    }

    public static string ReadRequiredResultString(string method, JsonElement envelope, string propertyName)
    {
        Validate(method, envelope);
        if (!envelope.GetProperty("result").TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new BrowserDevToolsException("malformed-result", method, null, "required-result-string-missing");
        }

        return value.GetString()!;
    }

    private static int? ReadCode(JsonElement error)
    {
        return error.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string? ReadString(JsonElement source, string propertyName)
    {
        return source.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string ReadTurnoverMessage(string message)
    {
        return string.Equals(message, "Cannot find context with specified id", StringComparison.Ordinal)
            || string.Equals(message, "Execution context was destroyed.", StringComparison.Ordinal)
            ? message
            : "protocol-error";
    }

    private static bool IsValidRemoteValue(string type, JsonElement value)
    {
        return type switch
        {
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "string" => value.ValueKind == JsonValueKind.String,
            "number" => value.ValueKind == JsonValueKind.Number,
            "bigint" => value.ValueKind == JsonValueKind.String,
            "object" => value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null,
            _ => false
        };
    }
}
