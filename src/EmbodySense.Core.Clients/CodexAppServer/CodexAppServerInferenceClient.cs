using EmbodySense.Core.Common.Inference;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Governance.Audit;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Inference.Profiles;
using EmbodySense.Core.Common.Inference.Profiles.Models;
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
    private const string ExpectedModelProvider = "openai";
    private const int MaxProtocolLineCharacters = 1_000_000;
    private static readonly TimeSpan _protocolReadTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan _defaultPostCheckpointWriteDeadline = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan _defaultLateTransportAuditDeadline = TimeSpan.FromSeconds(5);
    private readonly LlmInferenceClientOptions _options;
    private ICodexAppServerTransport? _transport;
    private readonly CodexAppServerToolBridge? _toolBridge;
    private readonly ICodexAppServerContextBuilder _contextBuilder;
    private readonly CodexAppServerRequestHandler _requestHandler;
    private readonly Action? _providerRequestStarted;
    private readonly string _runtimeDirectory;
    private readonly IAuditLog? _auditLog;
    private readonly bool _transportWasInjected;
    private readonly TimeSpan _postCheckpointWriteDeadline;
    private readonly TimeSpan _lateTransportAuditDeadline;
    private int _nextRequestId;
    private bool _initialized;
    private string? _threadId;
    private string? _threadModel;
    private string? _threadModelProvider;
    private bool _injectedTransportQuarantined;

    /// <summary>
    /// Initializes a new instance of the <see cref="CodexAppServerInferenceClient"/> type.
    /// </summary>
    /// <param name="options">The admitted model, sandbox, executable, and working-directory options.</param>
    /// <param name="toolBroker">The governed tool broker, or <see langword="null"/> to expose no EmbodySense commands.</param>
    /// <param name="transport">An injected protocol transport, or <see langword="null"/> to launch <c>codex app-server --stdio</c>.</param>
    /// <param name="auditLog">The audit sink for declined native app-server requests, or <see langword="null"/> when unavailable.</param>
    /// <param name="providerRequestStarted">An optional legacy dispatch notification. It is invoked at the pre-write boundary only when no durable callback is supplied.</param>
    /// <param name="postCheckpointWriteDeadline">An optional tighter server-owned write deadline; it cannot exceed the production default.</param>
    /// <param name="lateTransportAuditDeadline">An optional tighter detached-audit deadline; it cannot exceed the production default.</param>
    public CodexAppServerInferenceClient(
        LlmInferenceClientOptions options,
        IToolBroker? toolBroker = null,
        ICodexAppServerTransport? transport = null,
        IAuditLog? auditLog = null,
        Action? providerRequestStarted = null,
        TimeSpan? postCheckpointWriteDeadline = null,
        TimeSpan? lateTransportAuditDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _postCheckpointWriteDeadline = ValidateTighterDeadline(
            postCheckpointWriteDeadline,
            _defaultPostCheckpointWriteDeadline,
            nameof(postCheckpointWriteDeadline));
        _lateTransportAuditDeadline = ValidateTighterDeadline(
            lateTransportAuditDeadline,
            _defaultLateTransportAuditDeadline,
            nameof(lateTransportAuditDeadline));

        _options = options;
        _transport = transport;
        _transportWasInjected = transport is not null;
        _toolBridge = toolBroker is null ? null : new CodexAppServerToolBridge(toolBroker);
        _contextBuilder = new CodexAppServerContextBuilder(toolBroker?.AvailableCommands);
        _runtimeDirectory = CreateRuntimeDirectory();
        _auditLog = auditLog;
        _requestHandler = new CodexAppServerRequestHandler(_toolBridge, auditLog);
        _providerRequestStarted = providerRequestStarted;
    }

    private static TimeSpan ValidateTighterDeadline(TimeSpan? candidate, TimeSpan productionDefault, string parameterName)
    {
        var deadline = candidate ?? productionDefault;
        if (deadline <= TimeSpan.Zero || deadline > productionDefault)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"The deadline must be positive and no longer than the production default of {productionDefault.TotalSeconds:N0} seconds.");
        }

        return deadline;
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
        return GenerateCoreAsync(request, responseChunkHandler, cancellationToken, providerTransportCommitBoundary: null);
    }

    /// <inheritdoc />
    public Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        CancellationToken cancellationToken,
        InferenceProviderTransportCommitBoundary providerTransportCommitBoundary)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(providerTransportCommitBoundary);
        return GenerateCoreAsync(request, responseChunkHandler, cancellationToken, providerTransportCommitBoundary);
    }

    /// <summary>Starts and initializes the exact local app-server process without creating a provider thread or sending inference data.</summary>
    /// <remarks>This lets Startup retain the verified executable-package lease only after all launch-time code has been loaded.</remarks>
    public Task PrepareAsync(CancellationToken cancellationToken = default)
        => EnsureInitializedAsync(cancellationToken);

    private async Task<LlmInferenceResponse> GenerateCoreAsync(
        LlmInferenceRequest request,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        CancellationToken cancellationToken,
        InferenceProviderTransportCommitBoundary? providerTransportCommitBoundary)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfBoundedOutputTokenCeilingCannotBeForwarded(request.Options.MaxOutputTokenCount);

        try
        {
            int requestId;
            if (providerTransportCommitBoundary is null)
            {
                _toolBridge?.SetInferenceCorrelation(request.Correlation);
                _requestHandler.SetInferenceCorrelation(request.Correlation);
                await EnsureThreadAsync(request, cancellationToken);
                requestId = NextRequestId();
                await SendTurnStartAsync(request, requestId, cancellationToken, CommitLegacyProviderWriteAsync);
            }
            else
            {
                requestId = 0;
                await ExecuteProviderTransportCommitBoundaryAsync(
                    providerTransportCommitBoundary,
                    async token =>
                    {
                        _toolBridge?.SetInferenceCorrelation(request.Correlation);
                        _requestHandler.SetInferenceCorrelation(request.Correlation);
                        await EnsureThreadAsync(request, token);
                        requestId = NextRequestId();
                        await SendTurnStartAfterDispatchCheckpointAsync(request, requestId, token);
                    },
                    cancellationToken);
                _providerRequestStarted?.Invoke();
            }

            var streamedText = new StringBuilder();
            string? turnId = null;
            string? completedText = null;
            LlmInferenceUsageEvidence? observedUsage = null;
            var pendingUsageByTurn = new Dictionary<string, LlmInferenceUsageEvidence>(StringComparer.Ordinal);
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
                    ThrowIfTurnStartError(message);
                    turnStartResponseReceived = true;
                    turnId = TryGetNestedString(message, "result", "turn", "id") ?? turnId;
                    if (turnId is not null && pendingUsageByTurn.TryGetValue(turnId, out var pendingUsage))
                    {
                        observedUsage = pendingUsage;
                    }
                    pendingUsageByTurn.Clear();
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

                    case "thread/tokenUsage/updated":
                        var usageTurnId = TryGetNestedString(message, "params", "turnId");
                        if (usageTurnId is not null && IsCurrentTurnNotification(message, turnId))
                        {
                            var usage = ReadAuthoritativeTokenUsage(message);
                            if (turnId is null)
                            {
                                if (pendingUsageByTurn.ContainsKey(usageTurnId) || pendingUsageByTurn.Count < 32)
                                {
                                    pendingUsageByTurn[usageTurnId] = usage;
                                }
                            }
                            else
                            {
                                observedUsage = usage;
                            }
                        }

                        break;

                    case "model/rerouted":
                        if (IsCurrentOrPendingTurnNotification(message, turnId))
                        {
                            RejectModelReroute(message);
                        }

                        break;
                }
            }

            return new LlmInferenceResponse(
                completedText ?? streamedText.ToString(),
                LlmInferenceSurface.OpenAiCodex,
                observedUsage ?? LlmInferenceUsageEvidence.Unavailable("codex-app-server", "thread-token-usage-v2"),
                _threadModel,
                turnId,
                _threadModelProvider);
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
        _threadModel = null;
        _threadModelProvider = null;
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
        _threadModel = null;
        _threadModelProvider = null;
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
            var threadId = TryGetNestedString(message, "result", "thread", "id")
                ?? throw new InvalidOperationException("Codex app-server thread/start response did not include a thread id.");
            var model = TryGetNestedString(message, "result", "model")
                ?? throw new InvalidOperationException("Codex app-server thread/start response did not include the exact model.");
            var modelProvider = TryGetNestedString(message, "result", "modelProvider")
                ?? throw new InvalidOperationException("Codex app-server thread/start response did not include the exact model provider.");
            var threadModelProvider = TryGetNestedString(message, "result", "thread", "modelProvider")
                ?? throw new InvalidOperationException("Codex app-server thread/start response did not bind the thread to one exact model provider.");
            if (!string.Equals(model, _options.Model, StringComparison.Ordinal)
                || !string.Equals(modelProvider, ExpectedModelProvider, StringComparison.Ordinal)
                || !string.Equals(threadModelProvider, ExpectedModelProvider, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Codex app-server selected a model or provider outside the exact admitted configuration.");
            }

            _threadId = threadId;
            _threadModel = model;
            _threadModelProvider = modelProvider;
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
            ["modelProvider"] = ExpectedModelProvider,
            ["allowProviderModelFallback"] = false,
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

    private async Task SendRequestAsync(
        string method,
        int requestId,
        JsonObject parameters,
        CancellationToken cancellationToken,
        InferenceProviderTransportCommitBoundary? transportCommitBoundary = null)
    {
        await SendAsync(new JsonObject
        {
            ["id"] = requestId,
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken, transportCommitBoundary);
    }

    private async Task SendNotificationAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        await SendAsync(new JsonObject
        {
            ["method"] = method,
            ["params"] = parameters
        }, cancellationToken);
    }

    private async Task SendAsync(JsonObject message, CancellationToken cancellationToken, InferenceProviderTransportCommitBoundary? transportCommitBoundary = null)
    {
        var line = message.ToJsonString();
        if (transportCommitBoundary is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var guardedTransport = GetTransport();
            await ExecuteProviderTransportCommitBoundaryAsync(
                transportCommitBoundary,
                token => WriteAfterDispatchCheckpointAsync(guardedTransport, line, token),
                cancellationToken);
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var transport = GetTransport();
        await transport.WriteLineAsync(line, cancellationToken);
    }

    private Task SendTurnStartAsync(
        LlmInferenceRequest request,
        int requestId,
        CancellationToken cancellationToken,
        InferenceProviderTransportCommitBoundary transportCommitBoundary)
    {
        return SendRequestAsync("turn/start", requestId, CreateTurnStartParams(request), cancellationToken, transportCommitBoundary);
    }

    private async Task SendTurnStartAfterDispatchCheckpointAsync(
        LlmInferenceRequest request,
        int requestId,
        CancellationToken cancellationToken)
    {
        var message = new JsonObject
        {
            ["id"] = requestId,
            ["method"] = "turn/start",
            ["params"] = CreateTurnStartParams(request)
        };
        await WriteAfterDispatchCheckpointAsync(GetTransport(), message.ToJsonString(), cancellationToken);
    }

    private JsonObject CreateTurnStartParams(LlmInferenceRequest request)
    {
        return new JsonObject
        {
            ["threadId"] = _threadId,
            ["input"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = _contextBuilder.CreateTurnInput(request)
                }
            }
        };
    }

    private static void ThrowIfBoundedOutputTokenCeilingCannotBeForwarded(int? maximumOutputTokens)
    {
        if (maximumOutputTokens is null)
        {
            return;
        }

        throw new LlmInferenceTerminalFailureException($"Codex app-server does not support the exact output-token ceiling of {maximumOutputTokens}; the request is rejected before provider dispatch and no substitute is applied.");
    }

    private async Task ExecuteProviderTransportCommitBoundaryAsync(
        InferenceProviderTransportCommitBoundary transportCommitBoundary,
        Func<CancellationToken, Task> commitProviderDispatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = new object();
        var boundaryOpen = true;
        var commitCount = 0;
        var commitCompleted = 0;
        var commitSucceeded = 0;
        var boundaryReturnedBeforeCommitCompleted = 0;
        Task? firstCommit = null;
        var boundaryLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var boundaryLifetimeToken = boundaryLifetime.Token;

        Task CommitOnceAsync(CancellationToken token)
        {
            lock (gate)
            {
                if (!boundaryOpen)
                {
                    AbandonCurrentTransport();
                    return Task.FromException(new InvalidOperationException("The provider transport commit callback cannot be invoked after its boundary returns."));
                }

                if (Interlocked.Increment(ref commitCount) != 1)
                {
                    AbandonCurrentTransport();
                    return Task.FromException(new InvalidOperationException("The provider transport commit callback may be invoked at most once."));
                }

                firstCommit = CommitAndTrackAsync(token);
                return firstCommit;
            }
        }

        async Task CommitAndTrackAsync(CancellationToken token)
        {
            try
            {
                using var commitCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, boundaryLifetimeToken);
                await commitProviderDispatch(commitCancellation.Token);
                Volatile.Write(ref commitSucceeded, 1);
            }
            finally
            {
                Volatile.Write(ref commitCompleted, 1);
            }
        }

        try
        {
            await transportCommitBoundary(CommitOnceAsync, cancellationToken);
        }
        catch
        {
            if (Volatile.Read(ref commitCount) > 0)
            {
                AbandonCurrentTransport();
            }

            throw;
        }
        finally
        {
            lock (gate)
            {
                boundaryOpen = false;
                if (commitCount == 1 && Volatile.Read(ref commitCompleted) == 0)
                {
                    boundaryReturnedBeforeCommitCompleted = 1;
                }
            }

            try
            {
                await boundaryLifetime.CancelAsync();
            }
            catch (AggregateException)
            {
            }

            if (firstCommit is null || firstCommit.IsCompleted)
            {
                boundaryLifetime.Dispose();
            }
            else
            {
                _ = DisposeBoundaryLifetimeAfterCommitAsync(firstCommit, boundaryLifetime);
            }
        }

        var observedCommitCount = Volatile.Read(ref commitCount);
        if (observedCommitCount == 0)
        {
            throw new InvalidOperationException("The provider transport commit boundary returned without invoking its write callback.");
        }

        if (observedCommitCount != 1)
        {
            AbandonCurrentTransport();
            throw new InvalidOperationException("The provider transport commit callback may be invoked at most once.");
        }

        if (Volatile.Read(ref boundaryReturnedBeforeCommitCompleted) != 0)
        {
            AbandonCurrentTransport();
            throw new InvalidOperationException("The provider transport commit boundary returned before its write callback completed.");
        }

        if (Volatile.Read(ref commitSucceeded) == 0)
        {
            AbandonCurrentTransport();
            throw new InvalidOperationException("The provider transport commit boundary suppressed a failed transport write.");
        }
    }

    private static async Task DisposeBoundaryLifetimeAfterCommitAsync(Task commit, CancellationTokenSource boundaryLifetime)
    {
        try
        {
            await commit;
        }
        catch (Exception)
        {
        }
        finally
        {
            boundaryLifetime.Dispose();
        }
    }

    private async Task CommitLegacyProviderWriteAsync(Func<CancellationToken, Task> commitTransportWrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _providerRequestStarted?.Invoke();
        await commitTransportWrite(cancellationToken);
    }

    private async Task WriteAfterDispatchCheckpointAsync(ICodexAppServerTransport transport, string line, CancellationToken callerCancellationToken)
    {
        callerCancellationToken.ThrowIfCancellationRequested();
        using var writeCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken);
        writeCancellation.CancelAfter(_postCheckpointWriteDeadline);
        Task? writeTask = null;
        try
        {
            writeTask = transport.WriteLineAsync(line, writeCancellation.Token);
            await writeTask.WaitAsync(writeCancellation.Token);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            ObserveLateWrite(writeTask);
            AbandonAmbiguousTransport(transport);
            throw;
        }
        catch (OperationCanceledException) when (writeCancellation.IsCancellationRequested)
        {
            ObserveLateWrite(writeTask);
            AbandonAmbiguousTransport(transport);
            throw new TimeoutException("Codex app-server turn/start write exceeded the server-owned deadline after the durable dispatch checkpoint.");
        }
        catch (OperationCanceledException exception)
        {
            ObserveLateWrite(writeTask);
            AbandonAmbiguousTransport(transport);
            throw new IOException("Codex app-server turn/start write was interrupted after the durable dispatch checkpoint.", exception);
        }
        catch
        {
            ObserveLateWrite(writeTask);
            AbandonAmbiguousTransport(transport);
            throw;
        }
    }

    private void AbandonAmbiguousTransport(ICodexAppServerTransport transport)
    {
        if (!ReferenceEquals(_transport, transport))
        {
            return;
        }

        _transport = null;
        _threadId = null;
        _initialized = false;
        _nextRequestId = 0;
        _requestHandler.SetInferenceCorrelation(null);
        _toolBridge?.SetInferenceCorrelation(null);
        _injectedTransportQuarantined = _transportWasInjected;
        _ = DisposeAbandonedTransportAsync(transport);
    }

    private void AbandonCurrentTransport()
    {
        if (_transport is { } transport)
        {
            AbandonAmbiguousTransport(transport);
        }
    }

    private async Task DisposeAbandonedTransportAsync(ICodexAppServerTransport transport)
    {
        try
        {
            await transport.DisposeAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            await RecordLateTransportFailureAsync("dispose", exception);
        }
    }

    private void ObserveLateWrite(Task? writeTask)
    {
        if (writeTask is null || writeTask.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveLateWriteAsync(writeTask);
    }

    private async Task ObserveLateWriteAsync(Task writeTask)
    {
        try
        {
            await writeTask;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await RecordLateTransportFailureAsync("write", exception);
        }
    }

    private async Task RecordLateTransportFailureAsync(string operation, Exception exception)
    {
        if (_auditLog is null)
        {
            return;
        }

        using var auditCancellation = new CancellationTokenSource(_lateTransportAuditDeadline);
        Task? auditTask = null;
        try
        {
            auditTask = _auditLog.AppendAsync(AuditEvent.Create(
                actor: AuditSchema.Actors.Llm,
                action: AuditSchema.Actions.LlmAppServerRequest,
                target: "turn/start",
                outcome: AuditSchema.Outcomes.Failed,
                detail: "A detached Codex app-server transport operation failed after an ambiguous turn/start write.",
                metadata: new Dictionary<string, object?>
                {
                    ["operation"] = operation,
                    ["error_type"] = exception.GetType().Name
                }), auditCancellation.Token);
            await auditTask.WaitAsync(auditCancellation.Token);
        }
        catch (Exception)
        {
            ObserveLateAudit(auditTask);
        }
    }

    private static void ObserveLateAudit(Task? auditTask)
    {
        if (auditTask is null || auditTask.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveLateAuditAsync(auditTask);
    }

    private static async Task ObserveLateAuditAsync(Task auditTask)
    {
        try
        {
            await auditTask;
        }
        catch (Exception)
        {
        }
    }

    private async Task<JsonDocument> ReadMessageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var transport = GetTransport();
        // Link the caller token with a protocol deadline, then translate only deadline cancellation
        // into TimeoutException. Caller cancellation keeps its OperationCanceledException identity.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_protocolReadTimeout);
        string? line;
        try
        {
            line = await transport.ReadLineAsync(timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();
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

    private static void ThrowIfTurnStartError(JsonElement message)
    {
        if (!message.TryGetProperty("error", out var error))
        {
            return;
        }

        var errorMessage = TryGetString(error, "message") ?? error.GetRawText();
        var providerResponseId = TryGetNestedString(message, "error", "data", "turnId") ?? TryGetNestedString(message, "error", "data", "turn", "id");
        throw new LlmInferenceTerminalFailureException($"Codex app-server turn/start failed: {errorMessage}", providerResponseId);
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

    private static LlmInferenceUsageEvidence ReadAuthoritativeTokenUsage(JsonElement message)
    {
        if (!TryGetNestedElement(message, out var last, "params", "tokenUsage", "last")
            || !TryGetUsageQuantity(last, "inputTokens", out var inputTokens)
            || !TryGetUsageQuantity(last, "cachedInputTokens", out var cachedTokens)
            || !TryGetUsageQuantity(last, "outputTokens", out var outputTokens)
            || !TryGetUsageQuantity(last, "reasoningOutputTokens", out var reasoningOutputTokens)
            || !TryGetUsageQuantity(last, "totalTokens", out var totalTokens)
            || cachedTokens > inputTokens
            || reasoningOutputTokens > outputTokens
            || totalTokens != checked(inputTokens + outputTokens)
            || last.TryGetProperty("cacheWriteInputTokens", out var cacheWrite)
                && (cacheWrite.ValueKind != JsonValueKind.Number
                    || !cacheWrite.TryGetInt64(out var cacheWriteTokens)
                    || cacheWriteTokens < 0
                    || cacheWriteTokens > GovernedModelContractLimits.MaxTokens))
        {
            throw new FormatException("The correlated Codex token-usage notification was malformed or exceeded schema-1 bounds.");
        }

        return LlmInferenceUsageEvidence.Create(
            1,
            "codex-app-server",
            "thread-token-usage-v2",
            GovernedModelUsageMeasurement.Authoritative(inputTokens),
            GovernedModelUsageMeasurement.Authoritative(outputTokens),
            GovernedModelUsageMeasurement.Authoritative(cachedTokens),
            GovernedModelUsageMeasurement.Authoritative(totalTokens),
            GovernedModelMonetaryUsageMeasurement.Unavailable);
    }

    private bool IsCurrentOrPendingTurnNotification(JsonElement message, string? turnId)
    {
        var notificationThreadId = TryGetNestedString(message, "params", "threadId");
        var notificationTurnId = TryGetNestedString(message, "params", "turnId");
        return _threadId is not null
            && string.Equals(notificationThreadId, _threadId, StringComparison.Ordinal)
            && notificationTurnId is not null
            && (turnId is null || string.Equals(notificationTurnId, turnId, StringComparison.Ordinal));
    }

    private void RejectModelReroute(JsonElement message)
    {
        var fromModel = TryGetNestedString(message, "params", "fromModel");
        var toModel = TryGetNestedString(message, "params", "toModel");
        if (_threadModel is null
            || !string.Equals(fromModel, _threadModel, StringComparison.Ordinal)
            || !string.Equals(toModel, _threadModel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Codex app-server rerouted the current turn outside the exact admitted model.");
        }
    }

    private static bool TryGetUsageQuantity(JsonElement usage, string propertyName, out long value)
    {
        value = 0;
        return usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out value)
            && value >= 0
            && value <= GovernedModelContractLimits.MaxTokens;
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
