using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Projects the permitted <c>embodysense.command</c> surface and returns brokered outcomes in app-server JSON form.
/// </summary>
/// <remarks>
/// The bridge performs protocol-shape validation only; authority, permissions, approvals, audit, actuation, and retained
/// results remain owned by <see cref="IToolBroker"/>.
/// </remarks>
internal sealed class CodexAppServerToolBridge : ICodexAppServerToolBridge
{
    private const string Namespace = "embodysense";
    private readonly IToolBroker _toolBroker;
    private LlmInferenceCorrelation? _inferenceCorrelation;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexAppServerToolBridge"/> type.
    /// </summary>
    /// <param name="toolBroker">The tool broker.</param>
    public CodexAppServerToolBridge(IToolBroker toolBroker)
    {
        ArgumentNullException.ThrowIfNull(toolBroker);

        _toolBroker = toolBroker;
    }

    /// <summary>
    /// Gets the command set currently exposed by the governed broker.
    /// </summary>
    /// <value>The available commands tool commands.</value>
    public IReadOnlyList<ToolCommand> AvailableCommands => _toolBroker.AvailableCommands;

    /// <summary>
    /// Sets the serialized inference attempt whose governed tool calls are currently being handled.
    /// </summary>
    /// <param name="correlation">The current attempt correlation, or null outside generation.</param>
    public void SetInferenceCorrelation(LlmInferenceCorrelation? correlation)
    {
        _inferenceCorrelation = correlation;
    }

    /// <summary>
    /// Creates the single dynamic-tool declaration when at least one command is available.
    /// </summary>
    /// <returns>The JSON array.</returns>
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

    /// <summary>
    /// Converts one dynamic-tool request to a governed broker request and serializes its terminal result.
    /// </summary>
    /// <param name="parameters">The parameters.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the JSON object.</returns>
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

    private ToolRequest CreateToolRequest(JsonElement parameters)
    {
        var toolName = GetRequiredString(parameters, "tool");
        var toolNamespace = GetOptionalString(parameters, "namespace");

        if (!string.IsNullOrWhiteSpace(toolNamespace) && !string.Equals(toolNamespace, Namespace, StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The dynamic tool namespace is unsupported.");
        }

        if (!string.Equals(toolName, "command", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("The EmbodySense dynamic tool is unsupported.");
        }

        if (!parameters.TryGetProperty("arguments", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Dynamic tool call requires object arguments.");
        }

        var commandText = GetRequiredString(arguments, "command", "tool", "operation");

        if (!Enum.TryParse<ToolCommand>(commandText, ignoreCase: true, out var command) || !Enum.IsDefined(command))
        {
            throw new FormatException("The EmbodySense command is unsupported.");
        }

        var mutation = command is ToolCommand.Append or ToolCommand.Write or ToolCommand.Delete;
        if (mutation
            && (!HasExactProperties(arguments, "command", "input", "path")
                || arguments.GetProperty("command").ValueKind != JsonValueKind.String
                || !string.Equals(arguments.GetProperty("command").GetString(), ToolCommandFormatter.Format(command), StringComparison.Ordinal)
                || arguments.GetProperty("input").ValueKind != JsonValueKind.Object))
        {
            throw new FormatException("Workspace mutation arguments must use exactly the closed command, path, and input properties.");
        }

        var content = mutation ? arguments.GetProperty("input").GetRawText() : null;
        return new ToolRequest(
            command,
            mutation ? GetRequiredString(arguments, "path") : GetRequiredString(arguments, "path", "targetPath", "target"),
            content,
            GetOptionalString(arguments, "pattern", "query"),
            GetOptionalString(parameters, "callId"),
            _inferenceCorrelation?.ToolAuditCorrelation);
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
                ["description"] = "Exact workspace-relative target. Absolute, private, wildcard, and recursive targets are unsupported."
            },
            ["input"] = new JsonObject
            {
                ["description"] = "Required for append/write/delete. Its target must exactly equal path; literals are UTF-8 and credential references remain value-free.",
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["schemaVersion"] = new JsonObject { ["type"] = "integer", ["const"] = 1 },
                    ["scopeId"] = new JsonObject { ["type"] = "string", ["const"] = "workspace" },
                    ["target"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 512 },
                    ["precondition"] = CreatePreconditionSchema(),
                    ["segments"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = 32,
                        ["items"] = CreateContentSegmentSchema(),
                    },
                },
                ["required"] = new JsonArray("schemaVersion", "scopeId", "target", "precondition", "segments"),
                ["additionalProperties"] = false,
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
                ["additionalProperties"] = false,
                ["allOf"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["if"] = CommandCondition("append", "write"),
                        ["then"] = new JsonObject
                        {
                            ["required"] = new JsonArray("input"),
                            ["properties"] = new JsonObject
                            {
                                ["input"] = new JsonObject
                                {
                                    ["properties"] = new JsonObject
                                    {
                                        ["segments"] = new JsonObject { ["minItems"] = 1 },
                                    },
                                },
                            },
                        },
                    },
                    new JsonObject
                    {
                        ["if"] = CommandCondition("delete"),
                        ["then"] = new JsonObject
                        {
                            ["required"] = new JsonArray("input"),
                            ["properties"] = new JsonObject
                            {
                                ["input"] = new JsonObject
                                {
                                    ["properties"] = new JsonObject
                                    {
                                        ["segments"] = new JsonObject { ["maxItems"] = 0 },
                                    },
                                },
                            },
                        },
                    },
                },
            }
        };
    }

    private static JsonObject CreatePreconditionSchema()
        => new()
        {
            ["oneOf"] = new JsonArray
            {
                ClosedObject(
                    new JsonObject { ["kind"] = new JsonObject { ["type"] = "string", ["const"] = "expectedAbsent" } },
                    "kind"),
                ClosedObject(
                    new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["const"] = "expectedContentHash" },
                        ["expectedContentHash"] = HashSchema(),
                    },
                    "kind",
                    "expectedContentHash"),
                ClosedObject(
                    new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["const"] = "expectedGovernedVersion" },
                        ["expectedGovernedVersion"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["priorAfterEvidenceId"] = EvidenceIdSchema(),
                        ["priorAfterEvidenceHash"] = HashSchema(),
                    },
                    "kind",
                    "expectedGovernedVersion",
                    "priorAfterEvidenceId",
                    "priorAfterEvidenceHash"),
            },
        };

    private static JsonObject CreateContentSegmentSchema()
        => new()
        {
            ["oneOf"] = new JsonArray
            {
                ClosedObject(
                    new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["const"] = "literalUtf8" },
                        ["literal"] = new JsonObject { ["type"] = "string", ["maxLength"] = 16_384 },
                    },
                    "kind",
                    "literal"),
                ClosedObject(
                    new JsonObject
                    {
                        ["kind"] = new JsonObject { ["type"] = "string", ["const"] = "credentialReference" },
                        ["credentialReferenceId"] = EvidenceIdSchema(),
                    },
                    "kind",
                    "credentialReferenceId"),
            },
        };

    private static JsonObject ClosedObject(JsonObject properties, params string[] required)
        => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(value => (JsonNode?)value).ToArray()),
            ["additionalProperties"] = false,
        };

    private static JsonObject HashSchema()
        => new() { ["type"] = "string", ["pattern"] = "^[0-9a-f]{64}$" };

    private static JsonObject EvidenceIdSchema()
        => new() { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 160, ["pattern"] = "^[a-z0-9][a-z0-9._:/-]*$" };

    private static JsonObject CommandCondition(params string[] commands)
        => new()
        {
            ["properties"] = new JsonObject
            {
                ["command"] = new JsonObject
                {
                    ["enum"] = new JsonArray(commands.Select(value => (JsonNode?)value).ToArray()),
                },
            },
            ["required"] = new JsonArray("command"),
        };

    private JsonArray CreateCommandEnum()
    {
        var values = new JsonArray();
        foreach (var command in AvailableCommands)
        {
            values.Add(ToolCommandFormatter.Format(command));
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

    private static bool HasExactProperties(JsonElement element, params string[] expectedNames)
    {
        var names = element.EnumerateObject().Select(item => item.Name).ToArray();
        return names.Length == expectedNames.Length
            && names.Distinct(StringComparer.Ordinal).Count() == expectedNames.Length
            && names.Order(StringComparer.Ordinal).SequenceEqual(expectedNames.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

}
