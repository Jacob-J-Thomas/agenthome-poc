using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Audit;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Tools;

namespace EmbodySense.Core.Inference.AppServer;

internal sealed class CodexAppServerInferenceClient : ILlmInferenceClient, IResettableInferenceClient, IAsyncDisposable
{
    private const string ClientName = "embodysense";
    private const string ClientTitle = "EmbodySense";
    private const string ClientVersion = "0.1.0";
    private readonly LlmInferenceClientOptions _options;
    private ICodexAppServerTransport? _transport;
    private readonly ICodexAppServerToolBridge? _toolBridge;
    private readonly ICodexAppServerContextBuilder _contextBuilder;
    private readonly ICodexAppServerRequestHandler _requestHandler;
    private readonly string _runtimeDirectory;
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
        _contextBuilder = new CodexAppServerContextBuilder();
        _runtimeDirectory = CreateRuntimeDirectory();
        var auditLog = AuditLog.TryCreateForExistingWorkspace(options.WorkingDirectory);
        _requestHandler = new CodexAppServerRequestHandler(_toolBridge, auditLog);
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
            ["developerInstructions"] = _contextBuilder.CreateDeveloperInstructions(request),
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

    private async Task HandleServerRequestAsync(JsonElement message, CancellationToken cancellationToken)
    {
        var requestId = message.GetProperty("id").Clone();
        var method = GetRequiredString(message, "method");
        var parameters = message.GetProperty("params");
        var handling = await _requestHandler.HandleAsync(method, parameters, cancellationToken);
        if (!handling.Handled || handling.Result is null)
        {
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

        await SendAsync(new JsonObject
        {
            ["id"] = JsonNode.Parse(requestId.GetRawText()),
            ["result"] = handling.Result
        }, cancellationToken);
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
