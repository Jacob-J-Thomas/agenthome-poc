using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Tools;

namespace EmbodySense.Core.Inference.Implementations;

internal sealed class CodexAppServerInferenceClient : ILlmInferenceClient, IResettableInferenceClient, IAsyncDisposable
{
    private const string ClientName = "embodysense";
    private const string ClientTitle = "EmbodySense";
    private const string ClientVersion = "0.1.0";
    private const int MaxDeveloperInstructionContextCharacters = 24_000;
    private readonly LlmInferenceClientOptions _options;
    private ICodexAppServerTransport? _transport;
    private readonly CodexAppServerToolBridge? _toolBridge;
    private readonly string _runtimeDirectory;
    private readonly AuditLog? _auditLog;
    private int _nextRequestId;
    private bool _initialized;
    private string? _threadId;

    public CodexAppServerInferenceClient(
        LlmInferenceClientOptions options,
        IToolBroker? toolBroker = null,
        ICodexAppServerTransport? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _transport = transport;
        _toolBridge = toolBroker is null ? null : new CodexAppServerToolBridge(toolBroker);
        _runtimeDirectory = CreateRuntimeDirectory();
        _auditLog = AuditLog.TryCreateForExistingWorkspace(options.WorkingDirectory);
    }

    public async Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await EnsureThreadAsync(request, cancellationToken);

        var requestId = NextRequestId();
        var userText = GetLatestUserMessage(request);
        await SendRequestAsync("turn/start", requestId, new JsonObject
        {
            ["threadId"] = _threadId,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = userText
                }
            }
        }, cancellationToken);

        var streamedText = new StringBuilder();
        string? turnId = null;
        string? completedText = null;
        var turnStartResponseReceived = false;
        var turnCompleted = false;

        while (!turnStartResponseReceived || !turnCompleted)
        {
            using var messageDocument = await ReadMessageAsync(cancellationToken);
            var message = messageDocument.RootElement;

            if (IsServerRequest(message))
            {
                await HandleServerRequestAsync(message, cancellationToken);
                continue;
            }

            if (IsResponse(message, requestId))
            {
                ThrowIfError(message);
                turnStartResponseReceived = true;
                turnId = TryGetNestedString(message, "result", "turn", "id") ?? turnId;
                continue;
            }

            if (!IsNotification(message, out var method))
            {
                continue;
            }

            switch (method)
            {
                case "item/agentMessage/delta":
                    if (IsCurrentTurnNotification(message, turnId))
                    {
                        var delta = message.GetProperty("params").GetProperty("delta").GetString() ?? "";
                        streamedText.Append(delta);
                        if (responseChunkHandler is not null)
                        {
                            await responseChunkHandler(delta, cancellationToken);
                        }
                    }

                    break;

                case "turn/completed":
                    if (IsCurrentTurnNotification(message, turnId))
                    {
                        turnCompleted = true;
                        turnId = TryGetNestedString(message, "params", "turn", "id") ?? turnId;
                        completedText = TryExtractCompletedAgentMessage(message);
                        ThrowIfTurnFailed(message);
                    }

                    break;
            }
        }

        return new LlmInferenceResponse(
            completedText ?? streamedText.ToString(),
            LlmInferenceSurface.OpenAiCodex,
            _options.Model,
            turnId);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }

        try
        {
            if (Directory.Exists(_runtimeDirectory))
            {
                Directory.Delete(_runtimeDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void ResetConversation()
    {
        _threadId = null;
    }

    private async Task EnsureThreadAsync(LlmInferenceRequest request, CancellationToken cancellationToken)
    {
        if (_threadId is not null)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken);

        var requestId = NextRequestId();
        await SendRequestAsync("thread/start", requestId, CreateThreadStartParams(request), cancellationToken);

        while (_threadId is null)
        {
            using var messageDocument = await ReadMessageAsync(cancellationToken);
            var message = messageDocument.RootElement;

            if (IsServerRequest(message))
            {
                await HandleServerRequestAsync(message, cancellationToken);
                continue;
            }

            if (!IsResponse(message, requestId))
            {
                continue;
            }

            ThrowIfError(message);
            _threadId = TryGetNestedString(message, "result", "thread", "id")
                ?? throw new InvalidOperationException("Codex app-server thread/start response did not include a thread id.");
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var requestId = NextRequestId();
        await SendRequestAsync("initialize", requestId, new JsonObject
        {
            ["clientInfo"] = new JsonObject
            {
                ["name"] = ClientName,
                ["title"] = ClientTitle,
                ["version"] = ClientVersion
            },
            ["capabilities"] = new JsonObject
            {
                ["experimentalApi"] = true
            }
        }, cancellationToken);

        while (!_initialized)
        {
            using var messageDocument = await ReadMessageAsync(cancellationToken);
            var message = messageDocument.RootElement;

            if (IsServerRequest(message))
            {
                await HandleServerRequestAsync(message, cancellationToken);
                continue;
            }

            if (!IsResponse(message, requestId))
            {
                continue;
            }

            ThrowIfError(message);
            _initialized = true;
        }

        await SendNotificationAsync("initialized", new JsonObject(), cancellationToken);
    }

    private JsonObject CreateThreadStartParams(LlmInferenceRequest request)
    {
        var parameters = new JsonObject
        {
            ["cwd"] = _runtimeDirectory,
            ["developerInstructions"] = CreateDeveloperInstructions(request),
            ["ephemeral"] = true,
            ["approvalPolicy"] = CreateGranularApprovalPolicy(),
            ["sandbox"] = NormalizeSandboxMode(_options.CodexSandbox),
            ["config"] = CreateRestrictedConfig()
        };

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            parameters["model"] = _options.Model;
        }

        if (_toolBridge is not null)
        {
            parameters["dynamicTools"] = _toolBridge.CreateToolSpecs();
        }

        return parameters;
    }

    private static JsonObject CreateGranularApprovalPolicy()
    {
        return new JsonObject
        {
            ["granular"] = new JsonObject
            {
                ["mcp_elicitations"] = false,
                ["request_permissions"] = false,
                ["rules"] = false,
                ["sandbox_approval"] = false,
                ["skill_approval"] = false
            }
        };
    }

    private static JsonObject CreateRestrictedConfig()
    {
        return new JsonObject
        {
            ["features"] = new JsonObject
            {
                ["shell_tool"] = false,
                ["multi_agent"] = false,
                ["web_search"] = false
            },
            ["web_search"] = "disabled",
            ["default_permissions"] = ":read-only"
        };
    }

    private static string CreateDeveloperInstructions(LlmInferenceRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""
            You are running inside EmbodySense through the Codex app-server protocol.

            EmbodySense governs the user workspace. Do not use Codex-native shell, filesystem, MCP, browser, web-search, subagent, or permission-escalation tools for workspace actions. The app-server working directory is an inert runtime directory, not the user workspace.

            For any workspace action, use only the `embodysense.command` dynamic tool. It enforces `.agent/permissions.json`, routes approval when required, and writes EmbodySense audit events. Do not claim a workspace action succeeded until the corresponding EmbodySense tool result says it succeeded.
            """);

        var restoredContext = FormatRestoredContext(request);
        if (!string.IsNullOrWhiteSpace(restoredContext))
        {
            builder.AppendLine();
            builder.AppendLine(restoredContext);
        }

        return builder.ToString().TrimEnd();
    }

    private static string FormatRestoredContext(LlmInferenceRequest request)
    {
        var latestUserIndex = FindLatestUserMessageIndex(request.Messages);
        var contextMessages = request.Messages
            .Take(latestUserIndex)
            .Where(message => message.Role is LlmMessageRole.System or LlmMessageRole.User or LlmMessageRole.Assistant)
            .ToArray();

        if (contextMessages.Length == 0)
        {
            return "";
        }

        var builder = new StringBuilder();
        builder.AppendLine("Restored EmbodySense context for this fresh provider thread:");
        foreach (var message in contextMessages)
        {
            builder.AppendLine();
            builder.AppendLine($"[{message.Role.ToString().ToLowerInvariant()}]");
            builder.AppendLine(message.Content.Trim());
        }

        var context = builder.ToString().TrimEnd();
        return context.Length <= MaxDeveloperInstructionContextCharacters
            ? context
            : context[^MaxDeveloperInstructionContextCharacters..];
    }

    private static int FindLatestUserMessageIndex(IReadOnlyList<LlmMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == LlmMessageRole.User)
            {
                return i;
            }
        }

        return messages.Count;
    }

    private async Task HandleServerRequestAsync(JsonElement message, CancellationToken cancellationToken)
    {
        var requestId = message.GetProperty("id").Clone();
        var method = GetRequiredString(message, "method");
        var parameters = message.GetProperty("params");

        JsonObject? result = method switch
        {
            "item/tool/call" when _toolBridge is not null => await _toolBridge.HandleToolCallAsync(parameters, cancellationToken),
            "item/commandExecution/requestApproval" => new JsonObject { ["decision"] = "decline" },
            "item/fileChange/requestApproval" => new JsonObject { ["decision"] = "decline" },
            "applyPatchApproval" => new JsonObject { ["decision"] = "decline" },
            "execCommandApproval" => new JsonObject { ["decision"] = "decline" },
            "item/permissions/requestApproval" => new JsonObject
            {
                ["permissions"] = new JsonObject
                {
                    ["fileSystem"] = null,
                    ["network"] = null
                },
                ["scope"] = "turn",
                ["strictAutoReview"] = true
            },
            "mcpServer/elicitation/request" => new JsonObject
            {
                ["action"] = "decline",
                ["content"] = null
            },
            "item/tool/requestUserInput" => new JsonObject
            {
                ["answers"] = new JsonObject()
            },
            _ => null
        };

        if (result is null)
        {
            await RecordAppServerRequestAsync(method, parameters, AuditSchema.Outcomes.Failed, "Rejected unsupported Codex app-server request.", cancellationToken);
            await SendAsync(new JsonObject
            {
                ["id"] = JsonNode.Parse(requestId.GetRawText()),
                ["error"] = new JsonObject
                {
                    ["code"] = -32601,
                    ["message"] = $"EmbodySense does not support app-server request method `{method}`."
                }
            }, cancellationToken);
            return;
        }

        var outcome = GetHandledRequestOutcome(method, result);
        await RecordAppServerRequestAsync(method, parameters, outcome, GetHandledRequestDetail(method, outcome), cancellationToken);
        await SendAsync(new JsonObject
        {
            ["id"] = JsonNode.Parse(requestId.GetRawText()),
            ["result"] = result
        }, cancellationToken);
    }

    private Task RecordAppServerRequestAsync(
        string method,
        JsonElement parameters,
        string outcome,
        string detail,
        CancellationToken cancellationToken)
    {
        if (_auditLog is null)
        {
            return Task.CompletedTask;
        }

        return _auditLog.AppendAsync(AuditEvent.Create(
            actor: AuditSchema.Actors.Llm,
            action: AuditSchema.Actions.LlmAppServerRequest,
            target: method,
            outcome: outcome,
            detail: detail,
            metadata: CreateAppServerRequestMetadata(method, parameters)), cancellationToken);
    }

    private static Dictionary<string, object?> CreateAppServerRequestMetadata(string method, JsonElement parameters)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["method"] = method,
            ["thread_id"] = TryGetString(parameters, "threadId"),
            ["turn_id"] = TryGetString(parameters, "turnId"),
            ["item_id"] = TryGetString(parameters, "itemId"),
            ["call_id"] = TryGetString(parameters, "callId"),
            ["namespace"] = TryGetString(parameters, "namespace"),
            ["tool"] = TryGetString(parameters, "tool")
        };

        return metadata
            .Where(item => item.Value is not null)
            .ToDictionary(item => item.Key, item => item.Value);
    }

    private static string GetHandledRequestOutcome(string method, JsonObject result)
    {
        if (method != "item/tool/call")
        {
            return AuditSchema.Outcomes.Denied;
        }

        return IsSuccessfulToolResult(result) ? AuditSchema.Outcomes.Succeeded : AuditSchema.Outcomes.Failed;
    }

    private static string GetHandledRequestDetail(string method, string outcome)
    {
        if (method != "item/tool/call")
        {
            return "Declined native Codex app-server request.";
        }

        return outcome == AuditSchema.Outcomes.Succeeded
            ? "Handled Codex app-server dynamic tool request."
            : "Rejected Codex app-server dynamic tool request.";
    }

    private static bool IsSuccessfulToolResult(JsonObject result)
    {
        return result.TryGetPropertyValue("success", out var success) && success is not null && success.GetValue<bool>();
    }

    private async Task SendRequestAsync(string method, int requestId, JsonObject parameters, CancellationToken cancellationToken)
    {
        await SendAsync(new JsonObject
        {
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken);
    }

    private async Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        await SendAsync(new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken);
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await GetTransport().WriteLineAsync(message.ToJsonString(), cancellationToken);
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var transport = GetTransport();
        var line = await transport.ReadLineAsync(cancellationToken);

        if (line is null)
        {
            var detail = string.IsNullOrWhiteSpace(transport.ErrorOutput)
                ? "Codex app-server closed its output stream."
                : $"Codex app-server closed its output stream: {transport.ErrorOutput.Trim()}";
            throw new InvalidOperationException(detail);
        }

        return JsonDocument.Parse(line);
    }

    private ICodexAppServerTransport GetTransport()
    {
        return _transport ??= new CodexAppServerProcessTransport(_options, _runtimeDirectory);
    }

    private int NextRequestId()
    {
        return Interlocked.Increment(ref _nextRequestId);
    }

    private static bool IsServerRequest(JsonElement message)
    {
        return message.TryGetProperty("id", out _) && message.TryGetProperty("method", out _);
    }

    private static bool IsResponse(JsonElement message, int requestId)
    {
        return message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.GetInt32() == requestId;
    }

    private static bool IsNotification(JsonElement message, out string method)
    {
        method = "";

        if (!message.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        method = methodElement.GetString() ?? "";
        return !message.TryGetProperty("id", out _);
    }

    private bool IsCurrentTurnNotification(JsonElement message, string? turnId)
    {
        if (!message.TryGetProperty("params", out var parameters))
        {
            return false;
        }

        var notificationThreadId = TryGetString(parameters, "threadId");
        if (!string.Equals(notificationThreadId, _threadId, StringComparison.Ordinal))
        {
            return false;
        }

        if (turnId is null)
        {
            return true;
        }

        var notificationTurnId = TryGetString(parameters, "turnId") ?? TryGetNestedString(parameters, "turn", "id");
        return string.Equals(notificationTurnId, turnId, StringComparison.Ordinal);
    }

    private static void ThrowIfError(JsonElement message)
    {
        if (!message.TryGetProperty("error", out var error))
        {
            return;
        }

        var errorMessage = TryGetString(error, "message") ?? error.GetRawText();
        throw new InvalidOperationException($"Codex app-server request failed: {errorMessage}");
    }

    private static void ThrowIfTurnFailed(JsonElement message)
    {
        var status = TryGetNestedString(message, "params", "turn", "status");

        if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errorMessage = TryGetNestedString(message, "params", "turn", "error", "message") ?? "turn failed";
        throw new InvalidOperationException($"Codex app-server turn failed: {errorMessage}");
    }

    private static string? TryExtractCompletedAgentMessage(JsonElement message)
    {
        if (!TryGetNestedElement(message, out var items, "params", "turn", "items") || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? lastAgentMessage = null;
        string? lastFinalAnswer = null;

        foreach (var item in items.EnumerateArray())
        {
            if (!string.Equals(TryGetString(item, "type"), "agentMessage", StringComparison.Ordinal))
            {
                continue;
            }

            var text = TryGetString(item, "text");
            if (text is null)
            {
                continue;
            }

            lastAgentMessage = text;

            if (string.Equals(TryGetString(item, "phase"), "final_answer", StringComparison.Ordinal))
            {
                lastFinalAnswer = text;
            }
        }

        return lastFinalAnswer ?? lastAgentMessage;
    }

    private static string GetLatestUserMessage(LlmInferenceRequest request)
    {
        return request.Messages.LastOrDefault(message => message.Role == LlmMessageRole.User)?.Content
            ?? throw new InvalidOperationException("Codex app-server inference requires a user message.");
    }

    private static string NormalizeSandboxMode(string sandbox)
    {
        return sandbox switch
        {
            "read-only" or "workspace-write" or "danger-full-access" => sandbox,
            _ => "read-only"
        };
    }

    private static string CreateRuntimeDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "embodysense-app-server", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        return TryGetString(element, propertyName)
            ?? throw new FormatException($"Expected string property `{propertyName}`.");
    }

    private static string? TryGetNestedString(JsonElement element, params string[] path)
    {
        return TryGetNestedElement(element, out var value, path) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool TryGetNestedElement(JsonElement element, out JsonElement value, params string[] path)
    {
        value = element;

        foreach (var propertyName in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(propertyName, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }
}
