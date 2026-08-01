using EmbodySense.Core.Common.Inference;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Governance.Tools;

namespace EmbodySense.Core.Clients.CodexAppServer;

/// <summary>
/// Maps EmbodySense inference requests to the Codex app-server JSON protocol and projects streamed responses back to the runtime.
/// </summary>
/// <remarks>
/// The client lazily initializes one ephemeral app-server thread, handles server-initiated requests while awaiting responses,
/// and accepts only bounded newline-delimited JSON messages. Callers must serialize generation, reset, and disposal; the
/// mutable protocol correlation and thread state are not designed for concurrent use.
/// </remarks>
public sealed class CodexAppServerInferenceClient : ILlmInferenceClient, IResettableInferenceClient, IQuarantinableInferenceClient, IAsyncDisposable
{
    private const string ClientName = "embodysense";
    private const string ClientTitle = "EmbodySense";
    private const string ClientVersion = "0.1.0";
    private const int MaxProtocolLineCharacters = 1_000_000;
    private static readonly TimeSpan _protocolReadTimeout = TimeSpan.FromMinutes(2);
    private readonly LlmInferenceClientOptions _options;
    private ICodexAppServerTransport? _transport;
    private readonly CodexAppServerToolBridge? _toolBridge;
    private readonly ICodexAppServerContextBuilder _contextBuilder;
    private readonly CodexAppServerRequestHandler _requestHandler;
    private readonly Action? _providerRequestStarted;
    private readonly string _runtimeDirectory;
    private readonly bool _transportWasInjected;
    private int _nextRequestId;
    private bool _initialized;
    private string? _threadId;
    private bool _injectedTransportQuarantined;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexAppServerInferenceClient"/> type.
    /// </summary>
    /// <param name="options">The admitted model, sandbox, executable, and working-directory options.</param>
    /// <param name="toolBroker">The governed tool broker, or <see langword="null"/> to expose no EmbodySense commands.</param>
    /// <param name="transport">An injected protocol transport, or <see langword="null"/> to launch <c>codex app-server --stdio</c>.</param>
    /// <param name="auditLog">The audit sink for declined native app-server requests, or <see langword="null"/> when unavailable.</param>
    /// <param name="providerRequestStarted">An optional callback invoked immediately before <c>turn/start</c> is sent.</param>
    public CodexAppServerInferenceClient(
        LlmInferenceClientOptions options,
        IToolBroker? toolBroker = null,
        ICodexAppServerTransport? transport = null,
        IAuditLog? auditLog = null,
        Action? providerRequestStarted = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _transport = transport;
        _transportWasInjected = transport is not null;
        _toolBridge = toolBroker is null ? null : new CodexAppServerToolBridge(toolBroker);
        _contextBuilder = new CodexAppServerContextBuilder(toolBroker?.AvailableCommands);
        _runtimeDirectory = CreateRuntimeDirectory();
        _requestHandler = new CodexAppServerRequestHandler(_toolBridge, auditLog);
        _providerRequestStarted = providerRequestStarted;
    }

    /// <summary>
    /// Starts one app-server turn and waits for its correlated terminal response.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="responseChunkHandler">An optional ordered handler for each correlated agent-message delta.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The completed response, preferring terminal message text over the accumulated delta stream.</returns>
    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        return GenerateAsync(request, responseChunkHandler, cancellationToken, providerRequestStarting: null);
    }

    /// <inheritdoc />
    public async Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? providerRequestStarting)
    {
        ArgumentNullException.ThrowIfNull(request);

        _toolBridge?.SetInferenceCorrelation(request.Correlation);
        _requestHandler.SetInferenceCorrelation(request.Correlation);
        try
        {

            await EnsureThreadAsync(request, cancellationToken);

            var requestId = NextRequestId();
            var userText = _contextBuilder.CreateTurnInput(request);
            if (providerRequestStarting is not null)
            {
                await providerRequestStarting(cancellationToken);
            }

            _providerRequestStarted?.Invoke();
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

                // App-server requests may interleave with the turn/start response and notifications.
                // Answer them immediately so the protocol cannot deadlock while this turn awaits completion.
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
                            ThrowIfTurnFailed(message, turnId);
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
        finally
        {
            _requestHandler.SetInferenceCorrelation(null);
            _toolBridge?.SetInferenceCorrelation(null);
        }
    }

    /// <summary>
    /// Disposes the app-server transport and best-effort removes the ephemeral runtime directory.
    /// </summary>
    /// <returns>A task that represents the asynchronous operation.</returns>
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

    /// <summary>
    /// Forgets the current app-server thread so the next turn starts a new logical conversation.
    /// </summary>
    public void ResetConversation()
    {
        _threadId = null;
    }

    /// <summary>
    /// Disposes the live app-server transport and clears all protocol correlation after an ambiguous attempt.
    /// </summary>
    public async Task QuarantineAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = _transport;
        _transport = null;
        _threadId = null;
        _initialized = false;
        _nextRequestId = 0;
        _requestHandler.SetInferenceCorrelation(null);
        _toolBridge?.SetInferenceCorrelation(null);
        if (transport is not null)
        {
            await transport.DisposeAsync();
        }

        _injectedTransportQuarantined = _transportWasInjected;
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

            // Initialization can itself elicit server requests, so the client services them while it
            // waits for the correlated initialize response.
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
            // Native command, file-change, permission, MCP-elicitation, and subagent request surfaces
            // stay disabled or are declined. The dynamic embodysense.command bridge below is
            // EmbodySense's governed, audited workspace-action boundary.
            ["config"] = CreateRestrictedConfig()
        };

        if (!string.IsNullOrWhiteSpace(_options.Model))
        {
            parameters["model"] = _options.Model;
        }

        if (_toolBridge is not null)
        {
            var dynamicTools = _toolBridge.CreateToolSpecs();
            if (dynamicTools.Count > 0)
            {
                parameters["dynamicTools"] = dynamicTools;
            }
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
        // Link the caller token with a protocol deadline, then translate only deadline cancellation
        // into TimeoutException. Caller cancellation keeps its OperationCanceledException identity.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_protocolReadTimeout);
        string? line;
        try
        {
            line = await transport.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Timed out waiting for Codex app-server protocol message after {_protocolReadTimeout.TotalSeconds:N0} seconds.");
        }

        if (line is null)
        {
            var detail = string.IsNullOrWhiteSpace(transport.ErrorOutput)
                ? "Codex app-server closed its output stream."
                : $"Codex app-server closed its output stream: {transport.ErrorOutput.Trim()}";
            throw new InvalidOperationException(detail);
        }

        if (line.Length > MaxProtocolLineCharacters)
        {
            throw new InvalidOperationException($"Codex app-server protocol message exceeded {MaxProtocolLineCharacters} characters.");
        }

        return JsonDocument.Parse(line);
    }

    private ICodexAppServerTransport GetTransport()
    {
        if (_injectedTransportQuarantined)
        {
            throw new InvalidOperationException("The injected Codex app-server transport was quarantined and cannot be reused.");
        }

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
            // Notifications can arrive before turn/start supplies the id. Thread identity is still
            // mandatory, and public turns are serialized, so this temporary correlation is unambiguous.
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

    private static void ThrowIfTurnFailed(JsonElement message, string? providerResponseId)
    {
        var status = TryGetNestedString(message, "params", "turn", "status");

        if (!string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var errorMessage = TryGetNestedString(message, "params", "turn", "error", "message") ?? "turn failed";
        throw new LlmInferenceTerminalFailureException($"Codex app-server turn failed: {errorMessage}", providerResponseId);
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

        // Prefer the protocol's final-answer phase; the last generic agent message is only a fallback
        // for servers that omit a distinct final-answer item.
        return lastFinalAnswer ?? lastAgentMessage;
    }

    private static string NormalizeSandboxMode(string sandbox)
    {
        return sandbox switch
        {
            "read-only" or "workspace-write" or "danger-full-access" => sandbox,
            _ => throw new ArgumentException($"Unsupported Codex sandbox mode: {sandbox}", nameof(sandbox))
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
