using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Configuration.Models;
using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Web.Models;
using EmbodySense.Web.Services;

namespace EmbodySense.Web;

/// <summary>
/// Owns the process-wide Web projection of workspace status, one lazy agent runtime, conversation turns,
/// custom-loop operations, durable run recovery, and shutdown.
/// </summary>
/// <remarks>
/// Default-conversation turns are serialized. Custom-loop operations may share the retained runtime and
/// cross SignalR disconnects, but approval ownership remains bound to the initiating connection. A cancelled
/// conversation discards its runtime at the next safe boundary without disposing a runtime still used by a
/// custom operation. Evidence reads recover interrupted runs before returning state. The application container
/// owns this host and must dispose it asynchronously.
/// </remarks>
public sealed class WebAgentRuntimeHost : IAsyncDisposable, IWebLoopRuntimeInvoker
{
    private readonly WebRunOptions _options;
    private readonly string _configuredModel;
    private readonly WebApprovalCoordinator _approvalCoordinator;
    private readonly IWorkspaceInitializer _workspaceInitializer;
    private readonly WorkspaceStatusReader _statusReader;
    private readonly WorkspaceConfigurationReader _configurationReader;
    private readonly LoopRunInspectionFacade _loopRuns;
    private readonly DefaultConversationRequestReconciliationReader _conversationRequests;
    private readonly IAgentRuntimeConversationPublicationObserver? _conversationPublicationObserver;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _workspaceInitializationGate = new(1, 1);
    private readonly object _codexRuntimeStatusGate = new();
    private readonly object _turnCancellationGate = new();
    private readonly CancellationTokenSource _hostLifetimeCancellation = new();
    private Task<CodexRuntimeStatus>? _codexRuntimeStatusTask;
    private CancellationTokenSource? _turnCancellation;
    private AgentRuntime? _runtime;
    private TaskCompletionSource<bool>? _runtimeDiscardCompletion;
    private int _activeCustomRuntimeOperations;
    private bool _discardRuntimeWhenCustomOperationsComplete;
    private bool _loopRecoveryCompleted;
    private bool _preserveCurrentConversationOnNextRuntimeCreation = true;
    private int _disposed;

    /// <summary>
    /// Initializes a Web host with the production workspace initializer and no publication observer.
    /// </summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <exception cref="ArgumentException">The options do not provide a nonblank configured model.</exception>
    public WebAgentRuntimeHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator)
        : this(options, approvalCoordinator, WorkspaceInitializer.ForWeb(), null, null)
    {
    }

    /// <summary>
    /// Initializes a Web host with a previously verified compatible Codex runtime status.
    /// </summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <param name="codexRuntimeStatus">The compatible status bound to the exact configured model and executable request.</param>
    /// <exception cref="ArgumentException">The options or pre-resolved runtime status are incompatible.</exception>
    public WebAgentRuntimeHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator, CodexRuntimeStatus codexRuntimeStatus)
        : this(options, approvalCoordinator, WorkspaceInitializer.ForWeb(), null, codexRuntimeStatus ?? throw new ArgumentNullException(nameof(codexRuntimeStatus)))
    {
    }

    /// <summary>
    /// Initializes a Web host with an explicit workspace initializer and no publication observer.
    /// </summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <param name="workspaceInitializer">The workspace initializer used by explicit initialization requests.</param>
    /// <exception cref="ArgumentException">The options do not provide a nonblank configured model.</exception>
    public WebAgentRuntimeHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator, IWorkspaceInitializer workspaceInitializer)
        : this(options, approvalCoordinator, workspaceInitializer, null, null)
    {
    }

    /// <summary>
    /// Initializes a Web host with explicit workspace and durable-conversation publication dependencies.
    /// </summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <param name="workspaceInitializer">The workspace initializer used by explicit initialization requests.</param>
    /// <param name="conversationPublicationObserver">
    /// The observer notified after durable conversation publication, or <see langword="null"/> for no notification.
    /// </param>
    /// <exception cref="ArgumentException">The options do not provide a nonblank configured model.</exception>
    public WebAgentRuntimeHost(
        WebRunOptions options,
        WebApprovalCoordinator approvalCoordinator,
        IWorkspaceInitializer workspaceInitializer,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver)
        : this(options, approvalCoordinator, workspaceInitializer, conversationPublicationObserver, null)
    {
    }

    private WebAgentRuntimeHost(
        WebRunOptions options,
        WebApprovalCoordinator approvalCoordinator,
        IWorkspaceInitializer workspaceInitializer,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver,
        CodexRuntimeStatus? codexRuntimeStatus)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(approvalCoordinator);
        ArgumentNullException.ThrowIfNull(workspaceInitializer);
        if (string.IsNullOrWhiteSpace(options.Model))
        {
            throw new ArgumentException("Web runtime composition requires a nonblank configured model.", nameof(options));
        }
        if (codexRuntimeStatus is not null)
        {
            ValidatePreResolvedStatus(options, codexRuntimeStatus);
        }

        _options = options;
        _configuredModel = options.Model;
        _approvalCoordinator = approvalCoordinator;
        _workspaceInitializer = workspaceInitializer;
        _conversationPublicationObserver = conversationPublicationObserver;
        _statusReader = new WorkspaceStatusReader();
        _configurationReader = new WorkspaceConfigurationReader();
        _loopRuns = new LoopRunInspectionFacade(options.WorkingDirectory, WorkspaceActors.Web, AgentRuntimeSurface.Web.Id);
        _conversationRequests = new DefaultConversationRequestReconciliationReader(options.WorkingDirectory);
        if (codexRuntimeStatus is not null)
        {
            _codexRuntimeStatusTask = Task.FromResult(codexRuntimeStatus);
        }
    }

    /// <summary>
    /// Reads current Web binding and workspace status without creating a runtime.
    /// </summary>
    /// <returns>The current browser status projection.</returns>
    public WebStatus GetStatus()
    {
        return WebStatusFactory.Create(_options, _statusReader.Read(_options.WorkingDirectory));
    }

    /// <summary>
    /// Gets the custom-loop inference provider and explicitly configured model.
    /// </summary>
    /// <returns>An OpenAI Codex model snapshot with the required explicitly configured model.</returns>
    public LoopRunModelSnapshot GetCustomLoopModel()
    {
        return new LoopRunModelSnapshot("OpenAiCodex", _configuredModel);
    }

    /// <summary>
    /// Gets the complete canonical default-conversation transcript at a serialized turn boundary.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel gate waits, recovery, or durable history reads.</param>
    /// <returns>
    /// Null when the workspace is uninitialized or no transcript source remains; otherwise the complete
    /// persisted or in-memory transcript, including an empty transcript for a fresh initialized workspace.
    /// </returns>
    /// <remarks>
    /// The read waits for an active turn and any deferred runtime discard. Before first runtime creation it
    /// hydrates durable history directly so another process owning custom-loop execution does not block transcript access.
    /// </remarks>
    public async Task<IReadOnlyList<WebTranscriptMessage>?> GetCurrentTranscriptAsync(CancellationToken cancellationToken = default)
    {
        if (!_statusReader.Read(_options.WorkingDirectory).IsInitialized)
        {
            return null;
        }

        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            while (true)
            {
                Task? discardCompletion = null;
                await _runtimeGate.WaitAsync(cancellationToken);
                try
                {
                    if (_discardRuntimeWhenCustomOperationsComplete)
                    {
                        // A cancelled chat cannot reuse this runtime, but transcript hydration must not
                        // observe it between the final custom operation and its deferred disposal.
                        discardCompletion = _runtimeDiscardCompletion?.Task;
                    }
                    else
                    {
                        await EnsureLoopRecoveryUnderGateAsync(cancellationToken);
                        if (_runtime is null && _preserveCurrentConversationOnNextRuntimeCreation)
                        {
                            var persistedTranscript = await new ConversationTranscriptReader().ReadCurrentAsync(_options.WorkingDirectory, cancellationToken);
                            return persistedTranscript.Select(message => new WebTranscriptMessage(message.Role, message.Content)).ToArray();
                        }

                        return _runtime?.GetActiveConversationTranscript().Select(message => new WebTranscriptMessage(message.Role, message.Content)).ToArray();
                    }
                }
                finally
                {
                    _runtimeGate.Release();
                }

                if (discardCompletion is null)
                {
                    return null;
                }

                await discardCompletion.WaitAsync(cancellationToken);
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Idempotently and serially initializes the configured workspace for the Web actor.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel initialization.</param>
    /// <returns>The resulting Web status.</returns>
    public async Task<WebStatus> InitializeWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await _workspaceInitializationGate.WaitAsync(cancellationToken);
        try
        {
            var status = _statusReader.Read(_options.WorkingDirectory);
            if (status.IsInitialized)
            {
                return WebStatusFactory.Create(_options, status, "already-initialized");
            }

            await _workspaceInitializer.InitializeAsync(status.RootPath, cancellationToken);
            var initializedStatus = _statusReader.Read(_options.WorkingDirectory);
            var outcome = initializedStatus.IsInitialized ? "initialized" : initializedStatus.HasPartialScaffold ? "partial" : "failed";
            return WebStatusFactory.Create(_options, initializedStatus, outcome);
        }
        finally
        {
            _workspaceInitializationGate.Release();
        }
    }

    /// <summary>
    /// Reads effective workspace configuration and cached Codex runtime compatibility.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel waiting for discovery or reading configuration.</param>
    /// <returns>The read-only configuration snapshot.</returns>
    /// <remarks>Runtime discovery is shared and cached; caller cancellation stops only that caller's wait.</remarks>
    public async Task<WorkspaceConfigurationSnapshot> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var codexRuntimeStatus = await GetCodexRuntimeStatusAsync(cancellationToken);
        return await _configurationReader.ReadAsync(_options.WorkingDirectory, CreateRuntimeConfiguration(codexRuntimeStatus), cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and lists one bounded page of durable custom-loop summaries.
    /// </summary>
    /// <param name="maximumCount">The maximum summaries to return.</param>
    /// <param name="loopId">An optional loop identifier filter.</param>
    /// <param name="cursor">An optional opaque continuation cursor.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The requested summary page.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopRunSummaryPageSnapshot> GetLoopRunsAsync(int maximumCount = 50, string? loopId = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop run evidence");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.ListPageAsync(maximumCount, loopId, cursor, cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and gets one complete durable run snapshot.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The run snapshot, or null when no valid artifact exists.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopRunSnapshot?> GetLoopRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop run evidence");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.GetAsync(runId, cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and gets monitor-visible state plus its canonical artifact hash.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The monitor snapshot, or null when no valid artifact exists.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopRunMonitorSnapshot?> GetLoopRunMonitorAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("monitoring custom-loop run evidence");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.GetMonitorAsync(runId, cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and gets one durable invocation reconciliation record.
    /// </summary>
    /// <param name="operationId">The caller-owned invocation operation identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The operation snapshot, or null when no record exists.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopInvocationOperationSnapshot?> GetLoopInvocationOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reconciling custom-loop invocation evidence");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.GetInvocationOperationAsync(operationId, cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and gets one durable lifecycle-control reconciliation record.
    /// </summary>
    /// <param name="operationId">The caller-owned control operation identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The operation snapshot, or null when no record exists.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopControlOperationSnapshot?> GetLoopControlOperationAsync(string operationId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reconciling custom-loop control evidence");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.GetControlOperationAsync(operationId, cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and gets retained trace content for one run.
    /// </summary>
    /// <param name="runId">The run artifact identifier.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The retained trace snapshot, or null when no trace exists.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopTraceInspectionSnapshot?> GetLoopTraceAsync(string runId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop trace evidence");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.GetTraceAsync(runId, cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and gets retained-trace quota and usage.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel recovery or reading.</param>
    /// <returns>The current trace quota snapshot.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopTraceQuotaSnapshot> GetLoopTraceQuotaAsync(CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reading custom-loop trace quota");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.GetTraceQuotaAsync(cancellationToken);
    }

    /// <summary>
    /// Recovers interrupted runs and deletes retained trace content using optimistic, idempotent evidence.
    /// </summary>
    /// <param name="runId">The owning run artifact identifier.</param>
    /// <param name="expectedTraceHash">The exact trace-content hash observed by the caller.</param>
    /// <param name="operationId">The caller-owned deletion operation identity.</param>
    /// <param name="cancellationToken">The token used to cancel recovery or deletion.</param>
    /// <returns>The durable deletion disposition.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or recovery cannot safely proceed yet.</exception>
    public async Task<LoopTraceDeletionResponse> DeleteLoopTraceAsync(string runId, string expectedTraceHash, string operationId, CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("deleting custom-loop trace content");
        await EnsureLoopRecoveryAsync(cancellationToken);
        return await _loopRuns.DeleteTraceAsync(runId, expectedTraceHash, operationId, cancellationToken);
    }

    /// <summary>
    /// Invokes an exact saved custom-loop definition under one live browser connection's approval ownership.
    /// </summary>
    /// <param name="input">The invocation operation identity, definition binding, and context selection.</param>
    /// <param name="ownerConnectionId">The live SignalR connection that owns governed approvals.</param>
    /// <param name="cancellationToken">The caller token linked with host shutdown for the full operation.</param>
    /// <returns>The durable admission or rejection response.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or a compatible runtime cannot be created.</exception>
    /// <remarks>
    /// Runtime activity accounting is released in a finally block. Host disposal cancels admitted invocation
    /// execution and waits until the operation leaves the shared runtime before disposing it.
    /// </remarks>
    public async Task<LoopRunInvocationResponse> InvokeLoopAsync(LoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var runtime = await BeginCustomRuntimeOperationAsync(executionCancellation.Token);
        try
        {
            return await runtime.InvokeCustomLoopAsync(input, executionCancellation.Token);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>
    /// Pauses a custom-loop run through the shared runtime.
    /// </summary>
    /// <param name="input">The optimistic, idempotent pause request.</param>
    /// <param name="cancellationToken">The token used to cancel acquisition or control processing.</param>
    /// <returns>The durable lifecycle-control response.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or a runtime cannot be created.</exception>
    public async Task<LoopRunControlResponse> PauseLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.PauseCustomLoopAsync(input, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>
    /// Cancels a custom-loop run through the shared runtime.
    /// </summary>
    /// <param name="input">The optimistic, idempotent cancel request.</param>
    /// <param name="cancellationToken">The token used to cancel acquisition or control processing.</param>
    /// <returns>The durable lifecycle-control response.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or a runtime cannot be created.</exception>
    public async Task<LoopRunControlResponse> CancelLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.CancelCustomLoopAsync(input, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>
    /// Explicitly resumes a paused custom-loop run under one live browser connection's approval ownership.
    /// </summary>
    /// <param name="input">The optimistic, idempotent resume request.</param>
    /// <param name="ownerConnectionId">The live SignalR connection that owns governed approvals.</param>
    /// <param name="cancellationToken">The caller token linked with host shutdown for the full operation.</param>
    /// <returns>The durable lifecycle-control response.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or a compatible runtime cannot be created.</exception>
    /// <remarks>Runtime activity accounting is released even when resume fails or is cancelled.</remarks>
    public async Task<LoopRunControlResponse> ResumeLoopAsync(LoopRunControlInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var runtime = await BeginCustomRuntimeOperationAsync(executionCancellation.Token);
        try
        {
            return await runtime.ResumeCustomLoopAsync(input, executionCancellation.Token);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>
    /// Runs one supported static command or serialized default-conversation turn and emits typed Web events.
    /// </summary>
    /// <param name="message">The nonblank user message or supported static runtime command.</param>
    /// <param name="writeEventAsync">The ordered event sink for deltas, context, final state, or failure.</param>
    /// <param name="ownerConnectionId">
    /// The SignalR connection that owns governed approvals, or null to make approval-required tools reject safely.
    /// </param>
    /// <param name="cancellationToken">The caller token used to cancel gate waits, runtime work, and event writes.</param>
    /// <param name="requestId">An optional browser-owned idempotency identity.</param>
    /// <returns>The conclusive runtime disposition after the terminal event is written.</returns>
    /// <exception cref="ArgumentException">The message is null, empty, or whitespace.</exception>
    /// <exception cref="OperationCanceledException">
    /// Caller cancellation interrupts the turn. Cancellation requested through <see cref="CancelCurrentTurn"/>
    /// is instead represented by a cancellation event when its event write succeeds.
    /// </exception>
    /// <remarks>
    /// Static commands are handled before workspace initialization is required. A cancelled model turn marks
    /// the retained runtime for disposal so its session is not reused after an ambiguous cancellation boundary.
    /// Expected Codex compatibility failures are projected as bounded failure events.
    /// </remarks>
    public async Task<AgentRuntimeTurnResult> SendMessageAsync(
        string message,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        string? ownerConnectionId = null,
        CancellationToken cancellationToken = default,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentNullException.ThrowIfNull(writeEventAsync);

        await _turnGate.WaitAsync(cancellationToken);
        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var discardRuntime = false;
        SetTurnCancellation(turnCancellation);
        try
        {
            if (AgentRuntime.TryHandleStaticRuntimeCommand(message, out var staticCommandResult))
            {
                await WriteTurnResultAsync(staticCommandResult, writeEventAsync, cancellationToken);
                return staticCommandResult;
            }

            var runtime = await GetConversationRuntimeAsync(turnCancellation.Token);
            var turnResult = await runtime.RunTurnAsync(
                message,
                (chunk, token) =>
                {
                    return string.IsNullOrEmpty(chunk)
                        ? Task.CompletedTask
                        : writeEventAsync(WebStreamEvent.AssistantDelta(chunk), token);
                },
                (context, token) => writeEventAsync(WebStreamEvent.VerboseContext(context), token),
                cancellationToken: turnCancellation.Token,
                requestId: requestId);

            discardRuntime = turnResult.IsCancelled;
            await WriteTurnResultAsync(turnResult, writeEventAsync, cancellationToken);
            return turnResult;
        }
        catch (OperationCanceledException) when (turnCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            discardRuntime = true;
            var result = AgentRuntimeTurnResult.MessageCancelled("Message cancelled.");
            await WriteTurnResultAsync(result, writeEventAsync, cancellationToken);
            return result;
        }
        catch (CodexRuntimeUnavailableException exception)
        {
            var result = AgentRuntimeTurnResult.MessageFailed(exception.Message);
            await WriteTurnResultAsync(result, writeEventAsync, cancellationToken);
            return result;
        }
        finally
        {
            ClearTurnCancellation(turnCancellation);
            if (discardRuntime)
            {
                await DiscardRuntimeAsync();
            }

            _turnGate.Release();
        }
    }

    /// <summary>
    /// Reconciles one exact browser-owned request against durable default-conversation evidence.
    /// </summary>
    /// <param name="message">The exact canonical message retained by the browser.</param>
    /// <param name="requestId">The exact request identity retained with the message.</param>
    /// <param name="cancellationToken">The token used to cancel gate waits and durable recovery.</param>
    /// <returns>A bounded disposition that contains no transcript, provider, approval, or private runtime payload.</returns>
    public async Task<DefaultConversationRequestReconciliationSnapshot> ReconcileMessageAsync(
        string message,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        EnsureWorkspaceInitialized("reconciling a browser chat request");
        await _turnGate.WaitAsync(cancellationToken);
        try
        {
            return await _conversationRequests.ReadAsync(requestId, message, cancellationToken);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>
    /// Changes verbose context projection on the retained runtime and emits the resulting system message.
    /// </summary>
    /// <param name="enabled">Whether verbose context should be emitted during subsequent turns.</param>
    /// <param name="writeEventAsync">The event sink that receives the system result.</param>
    /// <param name="cancellationToken">The token used to cancel runtime acquisition or event writing.</param>
    /// <returns>A task that completes after the system event is written.</returns>
    /// <exception cref="InvalidOperationException">The workspace is not initialized or a compatible runtime cannot be created.</exception>
    public async Task SetVerboseModeAsync(
        bool enabled,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writeEventAsync);

        var runtime = await GetRuntimeAsync(cancellationToken);
        var result = runtime.SetVerbose(enabled);
        await writeEventAsync(WebStreamEvent.System(result.Output), cancellationToken);
    }

    /// <summary>
    /// Signals cancellation for the active default-conversation turn.
    /// </summary>
    /// <returns>
    /// True when an uncancelled active turn was signalled; false when no turn is active or cancellation was already requested.
    /// </returns>
    public bool CancelCurrentTurn()
    {
        lock (_turnCancellationGate)
        {
            if (_turnCancellation is null || _turnCancellation.IsCancellationRequested)
            {
                return false;
            }

            _turnCancellation.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Cancels host-owned invocation and resume work, waits for active custom operations, and disposes retained state.
    /// </summary>
    /// <returns>A value task that completes after runtime and run-inspection resources are disposed.</returns>
    /// <remarks>
    /// Disposal is idempotent. Conversation gate waits are not independently cancelled; application shutdown
    /// must first stop admitting request work through the ASP.NET host.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _hostLifetimeCancellation.Cancel();
        await DiscardRuntimeAsync(waitForCustomOperations: true);
        await _loopRuns.DisposeAsync();

        _runtimeGate.Dispose();
        _turnGate.Dispose();
        _workspaceInitializationGate.Dispose();
        _hostLifetimeCancellation.Dispose();
    }

    private async Task<AgentRuntime> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        EnsureWorkspaceInitialized("starting a runtime session");

        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            return await GetOrCreateRuntimeUnderGateAsync(cancellationToken);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task<AgentRuntime> GetConversationRuntimeAsync(CancellationToken cancellationToken)
    {
        EnsureWorkspaceInitialized("starting a runtime session");

        while (true)
        {
            Task? discardCompletion = null;
            await _runtimeGate.WaitAsync(cancellationToken);
            try
            {
                if (!_discardRuntimeWhenCustomOperationsComplete)
                {
                    return await GetOrCreateRuntimeUnderGateAsync(cancellationToken);
                }

                discardCompletion = _runtimeDiscardCompletion?.Task;
            }
            finally
            {
                _runtimeGate.Release();
            }

            if (discardCompletion is not null)
            {
                await discardCompletion.WaitAsync(cancellationToken);
            }
        }
    }

    private async Task<AgentRuntime> BeginCustomRuntimeOperationAsync(CancellationToken cancellationToken)
    {
        EnsureWorkspaceInitialized("starting a runtime session");

        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            var runtime = await GetOrCreateRuntimeUnderGateAsync(cancellationToken);
            // The runtime gate protects the counter transition, not the full operation. This lets lifecycle
            // control reach a run while another custom operation is awaiting inference or approval.
            _activeCustomRuntimeOperations++;
            return runtime;
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task EndCustomRuntimeOperationAsync()
    {
        await _runtimeGate.WaitAsync(CancellationToken.None);
        try
        {
            _activeCustomRuntimeOperations--;
            if (_activeCustomRuntimeOperations == 0 && _discardRuntimeWhenCustomOperationsComplete)
            {
                await DisposeRuntimeUnderGateAsync();
            }
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task<AgentRuntime> GetOrCreateRuntimeUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_runtime is null)
        {
            var codexRuntimeStatus = await GetCodexRuntimeStatusAsync(cancellationToken);
            if (codexRuntimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible)
            {
                throw new CodexRuntimeUnavailableException(codexRuntimeStatus);
            }

            var factory = _conversationPublicationObserver is null
                ? new AgentRuntimeFactory(_approvalCoordinator, codexRuntimeStatus)
                : new AgentRuntimeFactory(_approvalCoordinator, _conversationPublicationObserver, codexRuntimeStatus);
            var preserveCurrentConversation = _preserveCurrentConversationOnNextRuntimeCreation;
            _runtime = await factory.CreateAsync(
                _configuredModel,
                _options.WorkingDirectory,
                _options.CodexExecutablePath,
                _options.CodexSandbox,
                AgentRuntimeSurface.Web,
                preserveCurrentConversation,
                cancellationToken);
            _loopRecoveryCompleted = !_runtime.CustomLoopRecoveryRequired;
            _preserveCurrentConversationOnNextRuntimeCreation = false;
        }

        return _runtime;
    }

    private async Task EnsureLoopRecoveryAsync(CancellationToken cancellationToken)
    {
        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoopRecoveryUnderGateAsync(cancellationToken);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    private async Task EnsureLoopRecoveryUnderGateAsync(CancellationToken cancellationToken)
    {
        if (_loopRecoveryCompleted && (_runtime is null || !_runtime.CustomLoopRecoveryRequired))
        {
            return;
        }

        if (_runtime is not null)
        {
            if (!_runtime.CustomLoopRecoveryRequired)
            {
                _loopRecoveryCompleted = true;
                return;
            }

            _loopRecoveryCompleted = false;
            if (_activeCustomRuntimeOperations > 0)
            {
                // Recovery requires exclusive ownership of persisted execution state. Retire the retained
                // runtime at its next safe boundary and make this read retry instead of racing that state.
                _discardRuntimeWhenCustomOperationsComplete = true;
                _runtimeDiscardCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                throw new InvalidOperationException("custom_loop_recovery_pending: the retained runtime will be discarded when its active custom-loop operation reaches a safe boundary; retry the evidence request afterward.");
            }

            await DisposeRuntimeUnderGateAsync();
        }

        var recovery = await _loopRuns.RecoverInterruptedRunsAsync(cancellationToken);
        _loopRecoveryCompleted = recovery.Completed;
        _preserveCurrentConversationOnNextRuntimeCreation |= recovery.PreserveCurrentConversation;
    }

    private void EnsureWorkspaceInitialized(string operation)
    {
        if (!_statusReader.Read(_options.WorkingDirectory).IsInitialized)
        {
            throw new InvalidOperationException($"Workspace is not initialized. Initialize it from the web client before {operation}.");
        }
    }

    private async Task DiscardRuntimeAsync(bool waitForCustomOperations = false)
    {
        Task? discardCompletion = null;
        await _runtimeGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_activeCustomRuntimeOperations > 0)
            {
                // Conversation cancellation invalidates session reuse without interrupting a separately
                // admitted durable custom operation. Its final decrement owns the actual disposal.
                _discardRuntimeWhenCustomOperationsComplete = true;
                _runtimeDiscardCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                discardCompletion = _runtimeDiscardCompletion.Task;
            }
            else
            {
                await DisposeRuntimeUnderGateAsync();
            }
        }
        finally
        {
            _runtimeGate.Release();
        }

        if (waitForCustomOperations && discardCompletion is not null)
        {
            await discardCompletion;
        }
    }

    private async Task DisposeRuntimeUnderGateAsync()
    {
        var runtime = _runtime;
        var discardCompletion = _runtimeDiscardCompletion;
        // Any replacement runtime must reopen the same durable conversation rather than selecting a new one.
        _preserveCurrentConversationOnNextRuntimeCreation |= runtime is not null;
        _runtime = null;
        _runtimeDiscardCompletion = null;
        _discardRuntimeWhenCustomOperationsComplete = false;
        try
        {
            if (runtime is not null)
            {
                await runtime.DisposeAsync();
            }

            discardCompletion?.TrySetResult(true);
        }
        catch (Exception exception)
        {
            discardCompletion?.TrySetException(exception);
            throw;
        }
    }

    private Task<CodexRuntimeStatus> GetCodexRuntimeStatusAsync(CancellationToken cancellationToken)
    {
        Task<CodexRuntimeStatus> statusTask;
        lock (_codexRuntimeStatusGate)
        {
            _codexRuntimeStatusTask ??= new CodexRuntimeStatusReader().ReadAsync(_options.CodexExecutablePath, _configuredModel);
            statusTask = _codexRuntimeStatusTask;
        }

        return statusTask.WaitAsync(cancellationToken);
    }

    private static void ValidatePreResolvedStatus(WebRunOptions options, CodexRuntimeStatus codexRuntimeStatus)
    {
        if (codexRuntimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible || string.IsNullOrWhiteSpace(codexRuntimeStatus.ResolvedExecutablePath))
        {
            throw new ArgumentException("A pre-resolved Codex runtime status must identify a compatible executable.", nameof(codexRuntimeStatus));
        }

        if (!string.Equals(options.Model, codexRuntimeStatus.ConfiguredModel, StringComparison.Ordinal))
        {
            throw new ArgumentException("The pre-resolved Codex runtime status was produced for a different configured model.", nameof(codexRuntimeStatus));
        }

        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(options.CodexExecutablePath, codexRuntimeStatus.RequestedExecutablePath, pathComparison))
        {
            throw new ArgumentException("The pre-resolved Codex runtime status was produced for a different explicit executable request.", nameof(codexRuntimeStatus));
        }
    }

    private WorkspaceRuntimeConfiguration CreateRuntimeConfiguration(CodexRuntimeStatus codexRuntimeStatus)
    {
        var codexPath = codexRuntimeStatus.ResolvedExecutablePath ?? "not found";
        return new WorkspaceRuntimeConfiguration(
            AgentRuntimeSurface.Web.Id,
            _options.Url,
            _configuredModel,
            codexPath,
            _options.CodexSandbox,
            "Localhost web client is the primary browser surface; CLI remains available for verification.")
        {
            CodexRuntime = codexRuntimeStatus
        };
    }

    private static async Task WriteTurnResultAsync(
        AgentRuntimeTurnResult result,
        Func<WebStreamEvent, CancellationToken, Task> writeEventAsync,
        CancellationToken cancellationToken)
    {
        var commandOutputParts = new List<string>();
        foreach (var turnEvent in result.Events)
        {
            switch (turnEvent.Kind)
            {
                case AgentRuntimeTurnEventKind.TranscriptReplacement:
                    var messages = turnEvent.TranscriptMessages.Select(message => new WebTranscriptMessage(message.Role, message.Content)).ToArray();
                    await writeEventAsync(WebStreamEvent.HistoryLoaded(messages), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.CommandOutput:
                case AgentRuntimeTurnEventKind.Prompt:
                    // Static command output is projected as one final browser message so command prompts
                    // cannot be mistaken for streamed model deltas.
                    commandOutputParts.Add(turnEvent.Text);
                    break;

                case AgentRuntimeTurnEventKind.AssistantMessage:
                    await writeEventAsync(WebStreamEvent.AssistantFinal(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.Failure:
                    await writeEventAsync(WebStreamEvent.Failure(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.NeedsReview:
                    await writeEventAsync(WebStreamEvent.NeedsReview(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.Cancellation:
                    await writeEventAsync(WebStreamEvent.Cancelled(turnEvent.Text), cancellationToken);
                    break;

                case AgentRuntimeTurnEventKind.ExitRequested:
                    commandOutputParts.Add("The web client is still connected. Close the browser tab or stop the web server to leave.");
                    break;
            }
        }

        var output = string.Join(Environment.NewLine, commandOutputParts.Where(part => !string.IsNullOrWhiteSpace(part)));
        if (!string.IsNullOrWhiteSpace(output))
        {
            await writeEventAsync(WebStreamEvent.AssistantFinal(output), cancellationToken);
        }
    }

    private void SetTurnCancellation(CancellationTokenSource cancellation)
    {
        lock (_turnCancellationGate)
        {
            _turnCancellation = cancellation;
        }
    }

    private void ClearTurnCancellation(CancellationTokenSource cancellation)
    {
        lock (_turnCancellationGate)
        {
            if (ReferenceEquals(_turnCancellation, cancellation))
            {
                _turnCancellation = null;
            }
        }
    }
}
