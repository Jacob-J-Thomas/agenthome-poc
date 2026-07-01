using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

internal sealed class CodexAppServerToolBridge : ICodexAppServerToolBridge
{
    private const string Namespace = "embodysense";
    private readonly IToolBroker _toolBroker;

    public CodexAppServerToolBridge(IToolBroker toolBroker)
    {
        ArgumentNullException.ThrowIfNull(toolBroker);

        _toolBroker = toolBroker;
    }

    public IReadOnlyList<ToolCommand> AvailableCommands => _toolBroker.AvailableCommands;

    public JsonArray CreateToolSpecs()
    {
        if (AvailableCommands.Count == 0)
        {
            return [];
        }

        return
        [
            CreateCommandSpec()
        ];
    }

    public async Task<JsonObject> HandleToolCallAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        try
        {
            var request = CreateToolRequest(parameters);
            var result = await _toolBroker.ExecuteAsync(request, cancellationToken);

            return CreateToolResponse(result.Succeeded, ToolResultFormatter.FormatResults([result]));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or JsonException or InvalidOperationException)
        {
            return CreateToolResponse(false, $"EmbodySense tool call failed: {exception.Message}");
        }
    }

    private static ToolRequest CreateToolRequest(JsonElement parameters)
    {
        var toolName = GetRequiredString(parameters, "tool");
        var toolNamespace = GetOptionalString(parameters, "namespace");

        if (!string.IsNullOrWhiteSpace(toolNamespace) && !string.Equals(toolNamespace, Namespace, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Unsupported tool namespace: {toolNamespace}");
        }

        if (!string.Equals(toolName, "command", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Unsupported EmbodySense dynamic tool: {toolName}");
        }

        if (!parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Dynamic tool call requires object arguments.");
        }

        var commandText = GetRequiredString(arguments, "command", "tool", "operation");

        if (!Enum.TryParse<ToolCommand>(commandText, ignoreCase: true, out var command) || !Enum.IsDefined(command))
        {
            throw new FormatException($"Unsupported EmbodySense command: {commandText}");
        }

        return new ToolRequest(
            command,
            GetRequiredString(arguments, "path", "targetPath", "target"),
            GetOptionalString(arguments, "content", "text"),
            GetOptionalString(arguments, "pattern", "query"),
            GetOptionalString(parameters, "callId"));
    }

    private JsonObject CreateCommandSpec()
    {
        var properties = new JsonObject
        {
            ["command"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = CreateCommandEnum(),
                ["description"] = "Governed EmbodySense workspace command."
            },
            ["path"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Workspace-relative or absolute target path."
            },
            ["content"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Text content for write or append operations."
            },
            ["pattern"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Text pattern for search operations."
            }
        };

        return new JsonObject
        {
            ["name"] = "command",
            ["namespace"] = Namespace,
            ["description"] = "Run a governed EmbodySense workspace command through permission checks, approval routing, and audit logging.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray("command", "path"),
                ["additionalProperties"] = false
            }
        };
    }

    private JsonArray CreateCommandEnum()
    {
        var values = new JsonArray();
        foreach (var command in AvailableCommands)
        {
            values.Add(FormatCommand(command));
        }

        return values;
    }

    private static JsonObject CreateToolResponse(bool success, string text)
    {
        return new JsonObject
        {
            ["success"] = success,
            ["contentItems"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "inputText",
                    ["text"] = text
                }
            }
        };
    }

    private static string GetRequiredString(JsonElement element, params string[] propertyNames)
    {
        var value = GetOptionalString(element, propertyNames);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new FormatException($"Expected one of these string properties: {string.Join(", ", propertyNames)}.");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (TryGetProperty(element, propertyName, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement property)
    {
        foreach (var item in element.EnumerateObject())
        {
            if (string.Equals(item.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = item.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string FormatCommand(ToolCommand command)
    {
        return command.ToString().ToLowerInvariant();
    }
}
