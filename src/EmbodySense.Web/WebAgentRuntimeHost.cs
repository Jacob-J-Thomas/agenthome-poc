using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Startup.Configuration.Models;
using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Posture;
using EmbodySense.Core.Startup.Loops.GraphAuthoring.Models;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;
using EmbodySense.Core.Startup.Inference.Profiles.Models;
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
/// Default-conversation turns are serialized. Authoring borrows an inference-independent canonical run store for the
/// host lifetime, while custom-loop operations may share the retained runtime and
/// cross SignalR disconnects, but approval ownership remains bound to the initiating connection. A cancelled
/// conversation discards an unpinned runtime at the next safe boundary without disposing a runtime still used by a
/// custom operation. The process background host can instead pin that same runtime until it has stopped durable
/// admission. Evidence reads recover interrupted runs before returning state. The application container owns this host
/// and must dispose it asynchronously.
/// </remarks>
public sealed class WebAgentRuntimeHost : IAsyncDisposable, IWebLoopRuntimeInvoker
{
    private readonly WebRunOptions _options;
    private readonly string _configuredModel;
    private readonly WebApprovalCoordinator _approvalCoordinator;
    private readonly IWorkspaceInitializer _workspaceInitializer;
    private readonly string? _capabilityTrustRootPath;
    private readonly WorkspaceStatusReader _statusReader;
    private readonly WorkspaceConfigurationReader _configurationReader;
    private readonly CustomLoopRunStoreProvider _customLoopRunStoreProvider;
    private readonly LoopAuthoringFacade _loopAuthoring;
    private readonly LoopRunInspectionFacade _loopRuns;
    private readonly DefaultConversationRequestReconciliationReader _conversationRequests;
    private readonly IAgentRuntimeConversationPublicationObserver? _conversationPublicationObserver;
    private readonly Func<CodexRuntimeStatus, AgentRuntimeFactory>? _runtimeFactoryProvider;
    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly SemaphoreSlim _backgroundLifetimeGate = new(1, 1);
    private readonly SemaphoreSlim _authoringOperationGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly SemaphoreSlim _workspaceInitializationGate = new(1, 1);
    private readonly object _codexRuntimeStatusGate = new();
    private readonly object _turnCancellationGate = new();
    private readonly object _hostDisposalGate = new();
    private readonly CancellationTokenSource _hostLifetimeCancellation = new();
    private readonly object _backgroundStopGate = new();
    private Task<CodexRuntimeStatus>? _codexRuntimeStatusTask;
    private CancellationTokenSource? _turnCancellation;
    private AgentRuntime? _runtime;
    private TaskCompletionSource<bool>? _runtimeDiscardCompletion;
    private TaskCompletionSource<bool>? _authoringOperationDrainCompletion;
    private Task<AgentRuntimeGovernedLoopBackgroundStopResult>? _backgroundStopCompletion;
    private Task? _backgroundStopReleaseTask;
    private Task? _backgroundLifetimeDrainTask;
    private Task? _hostDisposalTask;
    private long _backgroundStopGeneration;
    private long _governedLoopBackgroundGeneration;
    private int _activeCustomRuntimeOperations;
    private int _activeAuthoringOperations;
    private bool _discardRuntimeWhenCustomOperationsComplete;
    private bool _governedLoopBackgroundRuntimePinned;
    private bool _loopRecoveryCompleted;
    private bool _preserveCurrentConversationOnNextRuntimeCreation = true;
    private AgentRuntimeGovernedLoopBackgroundStopResult? _lastBackgroundStopResult;
    private int _governedLoopBackgroundPosture = (int)WebGovernedLoopBackgroundPosture.Unavailable;
    private int _governedLoopBackgroundLivePeerStandby;
    private int _hostShutdownDeadlineElapsed;
    private int _loopRecoveryInProgress;
    private int _disposed;

    /// <summary>
    /// Initializes a Web host with the production workspace initializer and no publication observer.
    /// </summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <exception cref="ArgumentException">The options do not provide a nonblank configured model.</exception>
    public WebAgentRuntimeHost(WebRunOptions options, WebApprovalCoordinator approvalCoordinator)
        : this(options, approvalCoordinator, WorkspaceInitializer.ForWeb(), null, null, null, null)
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
        : this(options, approvalCoordinator, WorkspaceInitializer.ForWeb(), null, codexRuntimeStatus ?? throw new ArgumentNullException(nameof(codexRuntimeStatus)), null, null)
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
        : this(options, approvalCoordinator, workspaceInitializer, null, null, null, null)
    {
    }

    /// <summary>Initializes a Web host whose workspace initialization and retained runtime share one explicit server-owned capability trust root.</summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <param name="workspaceInitializer">The workspace initializer bound to the same trust root.</param>
    /// <param name="capabilityTrustRootPath">The explicit server-owned capability trust root used by retained runtime composition.</param>
    /// <exception cref="ArgumentException">The options do not provide a model or the trust-root path is blank.</exception>
    public WebAgentRuntimeHost(
        WebRunOptions options,
        WebApprovalCoordinator approvalCoordinator,
        IWorkspaceInitializer workspaceInitializer,
        string capabilityTrustRootPath)
        : this(options, approvalCoordinator, workspaceInitializer, null, null, RequireTrustRootPath(capabilityTrustRootPath), null)
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
        : this(options, approvalCoordinator, workspaceInitializer, conversationPublicationObserver, null, null, null)
    {
    }

    /// <summary>
    /// Initializes a Web host with explicit workspace, publication, and runtime-factory composition dependencies.
    /// </summary>
    /// <param name="options">The validated Web host and runtime options.</param>
    /// <param name="approvalCoordinator">The connection-owned governed approval coordinator.</param>
    /// <param name="workspaceInitializer">The workspace initializer used by explicit initialization requests.</param>
    /// <param name="conversationPublicationObserver">The optional durable-conversation publication observer.</param>
    /// <param name="runtimeFactoryProvider">Creates the runtime factory from the exact compatible runtime status.</param>
    public WebAgentRuntimeHost(
        WebRunOptions options,
        WebApprovalCoordinator approvalCoordinator,
        IWorkspaceInitializer workspaceInitializer,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver,
        Func<CodexRuntimeStatus, AgentRuntimeFactory> runtimeFactoryProvider)
        : this(
            options,
            approvalCoordinator,
            workspaceInitializer,
            conversationPublicationObserver,
            null,
            null,
            runtimeFactoryProvider ?? throw new ArgumentNullException(nameof(runtimeFactoryProvider)))
    {
    }

    private WebAgentRuntimeHost(
        WebRunOptions options,
        WebApprovalCoordinator approvalCoordinator,
        IWorkspaceInitializer workspaceInitializer,
        IAgentRuntimeConversationPublicationObserver? conversationPublicationObserver,
        CodexRuntimeStatus? codexRuntimeStatus,
        string? capabilityTrustRootPath,
        Func<CodexRuntimeStatus, AgentRuntimeFactory>? runtimeFactoryProvider)
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
        _capabilityTrustRootPath = capabilityTrustRootPath;
        _conversationPublicationObserver = conversationPublicationObserver;
        _runtimeFactoryProvider = runtimeFactoryProvider;
        _statusReader = new WorkspaceStatusReader();
        _configurationReader = new WorkspaceConfigurationReader();
        _customLoopRunStoreProvider = new CustomLoopRunStoreProvider(options.WorkingDirectory);
        _loopAuthoring = _customLoopRunStoreProvider.CreateLoopAuthoringFacade();
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
        return WebStatusFactory.Create(
            _options,
            _statusReader.Read(_options.WorkingDirectory),
            backgroundPosture: GovernedLoopBackgroundPosture);
    }

    internal WebGovernedLoopBackgroundPosture GovernedLoopBackgroundPosture
        => (WebGovernedLoopBackgroundPosture)Volatile.Read(ref _governedLoopBackgroundPosture);

    internal void SetGovernedLoopBackgroundPosture(WebGovernedLoopBackgroundPosture posture)
    {
        Volatile.Write(ref _governedLoopBackgroundPosture, (int)posture);
    }

    /// <summary>
    /// Signals process shutdown to host-owned governed operations before their retained runtime is drained or released.
    /// </summary>
    /// <remarks>
    /// The signal is idempotent and intentionally separate from <see cref="DisposeAsync"/> so the hosted lifetime can
    /// cancel approval and provider work before it begins draining the canonical background coordinator.
    /// </remarks>
    internal void SignalHostShutdown()
    {
        try
        {
            _hostLifetimeCancellation.Cancel();
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
            // A concurrent or prior host disposal already delivered the shutdown signal.
        }
    }

    /// <summary>
    /// Records that the hosted shutdown deadline elapsed while a safe-boundary drain remains active.
    /// </summary>
    /// <remarks>
    /// Container disposal must not then wait indefinitely for a stalled admitted operation. The host retains its
    /// synchronization resources until the already-started safe-boundary cleanup completes.
    /// </remarks>
    internal void MarkHostShutdownDeadlineElapsed()
    {
        Volatile.Write(ref _hostShutdownDeadlineElapsed, 1);
    }

    internal async Task WaitForConversationTurnsAsync(CancellationToken cancellationToken = default)
    {
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        _turnGate.Release();
    }

    internal async Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartGovernedLoopLocalBackgroundForProcessAsync()
    {
        _hostLifetimeCancellation.Token.ThrowIfCancellationRequested();
        if (!_statusReader.Read(_options.WorkingDirectory).IsInitialized)
        {
            return new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.None,
                true,
                "governed_local_background_unavailable: the Web workspace is not initialized.");
        }

        await _backgroundLifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            AgentRuntime runtime;
            while (true)
            {
                Task? discardCompletion = null;
                await _runtimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                    if (Volatile.Read(ref _loopRecoveryInProgress) != 0)
                    {
                        return new AgentRuntimeGovernedLoopBackgroundStartResult(
                            AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                            AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                            AgentRuntimeGovernedLoopBackgroundOwnership.None,
                            true,
                            "governed_local_background_recovery_pending: durable custom-loop recovery still owns the retained runtime boundary; retry background activation after recovery completes.");
                    }

                    if (!_discardRuntimeWhenCustomOperationsComplete)
                    {
                        var createdRuntime = _runtime is null;
                        if (!_governedLoopBackgroundRuntimePinned)
                        {
                            // This unpinned-to-pinned attempt starts a new Web background lifetime. Reset every prior
                            // stop completion and release boundary here so a delayed terminal callback cannot release
                            // or project posture for this generation.
                            BeginNewGovernedLoopLocalBackgroundLifetime();
                        }

                        runtime = await GetOrCreateRuntimeUnderGateAsync(CancellationToken.None).ConfigureAwait(false);
                        if (_hostLifetimeCancellation.IsCancellationRequested)
                        {
                            if (createdRuntime)
                            {
                                await DisposeRuntimeUnderGateAsync().ConfigureAwait(false);
                            }

                            _hostLifetimeCancellation.Token.ThrowIfCancellationRequested();
                        }

                        _governedLoopBackgroundRuntimePinned = true;
                        break;
                    }

                    discardCompletion = _runtimeDiscardCompletion?.Task;
                }
                finally
                {
                    _runtimeGate.Release();
                }

                await (discardCompletion ?? throw new InvalidOperationException("The pending runtime discard did not retain a completion boundary.")).ConfigureAwait(false);
            }

            var start = await runtime.StartGovernedLoopLocalBackgroundWithStatusAsync(CancellationToken.None).ConfigureAwait(false);
            Volatile.Write(ref _governedLoopBackgroundLivePeerStandby, start.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer ? 1 : 0);
            return start;
        }
        finally
        {
            _backgroundLifetimeGate.Release();
        }
    }

    internal async Task<AgentRuntimeGovernedLoopBackgroundStatus> ReadGovernedLoopLocalBackgroundForProcessAsync()
    {
        await _runtimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _loopRecoveryInProgress) != 0)
            {
                return new AgentRuntimeGovernedLoopBackgroundStatus(
                    AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundOwnership.None,
                    "governed_local_background_recovery_pending: durable custom-loop recovery owns the retained runtime boundary; retry the status read after recovery completes.");
            }

            var runtime = _governedLoopBackgroundRuntimePinned ? _runtime : null;
            return runtime is null
                ? new AgentRuntimeGovernedLoopBackgroundStatus(
                    AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                    AgentRuntimeGovernedLoopBackgroundOwnership.None,
                    "governed_local_background_unavailable: the Web process has not retained a canonical runtime.")
                : await runtime.ReadGovernedLoopLocalBackgroundStatusAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    internal async Task<AgentRuntimeGovernedLoopBackgroundStopResult> StopGovernedLoopLocalBackgroundForProcessAsync()
    {
        AgentRuntime? runtime;
        long generation;
        await _backgroundLifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _runtimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                runtime = _governedLoopBackgroundRuntimePinned ? _runtime : null;
                generation = Volatile.Read(ref _governedLoopBackgroundGeneration);
            }
            finally
            {
                _runtimeGate.Release();
            }

            var result = runtime is null
                ? new AgentRuntimeGovernedLoopBackgroundStopResult(
                    AgentRuntimeGovernedLoopBackgroundStopStatus.AlreadyStopped,
                    AgentRuntimeGovernedLoopBackgroundReadiness.Stopped,
                    AgentRuntimeGovernedLoopBackgroundOwnership.None,
                    "governed_local_background_stopped: the Web process did not retain a canonical runtime.")
                : await runtime.StopGovernedLoopLocalBackgroundAsync(CancellationToken.None).ConfigureAwait(false);
            RememberBackgroundStop(runtime, result, generation);
            return result;
        }
        finally
        {
            _backgroundLifetimeGate.Release();
        }
    }

    internal async Task ReleaseGovernedLoopLocalBackgroundForProcessAsync(bool retainRuntime = false, long? expectedGeneration = null)
    {
        Task<AgentRuntimeGovernedLoopBackgroundStopResult>? stopCompletion;
        lock (_backgroundStopGate)
        {
            if (!MatchesBackgroundGeneration(expectedGeneration))
            {
                return;
            }

            stopCompletion = _backgroundStopCompletion;
        }

        if (stopCompletion is not null)
        {
            var completedStop = await stopCompletion.ConfigureAwait(false);
            if (completedStop.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Draining)
            {
                return;
            }
        }

        Task? discardCompletion = null;
        await _backgroundLifetimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!MatchesBackgroundGeneration(expectedGeneration))
            {
                return;
            }

            await _runtimeGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!_governedLoopBackgroundRuntimePinned)
                {
                    return;
                }

                _governedLoopBackgroundRuntimePinned = false;
                if (retainRuntime)
                {
                    return;
                }

                if (_activeCustomRuntimeOperations > 0)
                {
                    _discardRuntimeWhenCustomOperationsComplete = true;
                    _runtimeDiscardCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    discardCompletion = _runtimeDiscardCompletion.Task;
                }
                else
                {
                    await DisposeRuntimeUnderGateAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _runtimeGate.Release();
            }
        }
        finally
        {
            _backgroundLifetimeGate.Release();
        }

        if (discardCompletion is not null)
        {
            await discardCompletion.ConfigureAwait(false);
        }
    }

    internal Task BeginGovernedLoopLocalBackgroundStopReleaseAsync()
    {
        lock (_backgroundStopGate)
        {
            var generation = Volatile.Read(ref _governedLoopBackgroundGeneration);
            return _backgroundStopReleaseTask ??= CompleteGovernedLoopLocalBackgroundStopReleaseAsync(generation);
        }
    }

    /// <summary>
    /// Registers the one hosted drain task that can still use host lifetime gates after an expired shutdown deadline.
    /// </summary>
    /// <param name="drainTask">The exact hosted drain task that owns the retained coordinator boundary.</param>
    internal void RegisterGovernedLoopLocalBackgroundLifetimeDrain(Task drainTask)
    {
        ArgumentNullException.ThrowIfNull(drainTask);
        lock (_backgroundStopGate)
        {
            _backgroundLifetimeDrainTask ??= drainTask;
        }
    }

    private async Task CompleteGovernedLoopLocalBackgroundStopReleaseAsync(long expectedGeneration)
    {
        AgentRuntimeGovernedLoopBackgroundStopResult? completedStop;
        Task<AgentRuntimeGovernedLoopBackgroundStopResult>? stopCompletion;
        bool wasLivePeerStandby;
        lock (_backgroundStopGate)
        {
            if (!MatchesBackgroundGeneration(expectedGeneration))
            {
                return;
            }

            stopCompletion = _backgroundStopCompletion;
            completedStop = _lastBackgroundStopResult;
            wasLivePeerStandby = Volatile.Read(ref _governedLoopBackgroundLivePeerStandby) != 0;
        }

        if (stopCompletion is not null)
        {
            completedStop = await stopCompletion.ConfigureAwait(false);
        }

        if (completedStop is null
            || completedStop.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Draining
            || !MatchesBackgroundGeneration(expectedGeneration))
        {
            return;
        }

        await ReleaseGovernedLoopLocalBackgroundForProcessAsync(expectedGeneration: expectedGeneration).ConfigureAwait(false);
        if (!MatchesBackgroundGeneration(expectedGeneration))
        {
            return;
        }

        SetGovernedLoopBackgroundPosture(ToWebReleasePosture(completedStop, wasLivePeerStandby));
    }

    /// <summary>
    /// Gets the custom-loop inference provider and explicitly configured model.
    /// </summary>
    /// <returns>An OpenAI Codex model snapshot with the required explicitly configured model.</returns>
    public LoopRunModelSnapshot GetCustomLoopModel()
    {
        return new LoopRunModelSnapshot("OpenAiCodex", _configuredModel);
    }

    internal async Task<T> UseLoopAuthoringAsync<T>(Func<LoopAuthoringFacade, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        EnsureWorkspaceInitialized("authoring custom loops");
        await BeginAuthoringOperationAsync(cancellationToken);
        try
        {
            return await operation(_loopAuthoring);
        }
        finally
        {
            await EndAuthoringOperationAsync();
        }
    }

    internal async Task<(string Status, object Payload)> ReadGovernedLoopOperationalPostureAsync(
        int maximumQueueEntries,
        int maximumSchedules,
        int maximumWakes,
        int maximumRuns,
        string? queueCursor,
        string? afterScheduleId,
        string? afterCheckpointId,
        string? afterRunId,
        CancellationToken cancellationToken)
    {
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            var result = await runtime.GovernedLoopOperations.ReadAsync(
                maximumQueueEntries,
                maximumSchedules,
                maximumWakes,
                maximumRuns,
                queueCursor,
                afterScheduleId,
                afterCheckpointId,
                afterRunId,
                cancellationToken);
            return (result.Status.ToString(), result);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<(string Status, object Payload)> ControlGovernedLoopOperationAsync(
        LoopOperationalControlRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            var result = await runtime.GovernedLoopOperations.ControlAsync(
                request.OperationId,
                request.Kind,
                request.TargetId,
                request.ExpectedRevision,
                request.ExpectedEvidenceHash,
                request.ExpectedAuthorityEvidenceHash,
                request.MaximumBatchItems,
                cancellationToken);
            return (result.Status.ToString(), result);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<GovernedLoopGraphCatalogResponse> ReadGovernedLoopGraphCatalogAsync(
        CancellationToken cancellationToken)
    {
        var runtime = await BeginGraphAuthoringRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.GovernedLoopGraphAuthoring.ReadCatalogAsync(cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<GovernedLoopRetryPolicyPreviewResponse> PreviewGovernedLoopRetryPolicyAsync(
        GovernedLoopRetryPolicyPreviewInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            return runtime.GovernedLoopGraphAuthoring.PreviewRetryPolicy(input);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<ModelProfileCatalogResponse> ReadModelProfilesAsync(
        string? startAfterId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.ModelProfiles.ReadAsync(startAfterId, maximumCount, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<ModelProfileRoutingPreviewResponse> PreviewModelRoutingAsync(
        ModelProfileRoutingPreviewInput input,
        CancellationToken cancellationToken)
    {
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.ModelProfiles.PreviewAsync(input, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<GovernedLoopGraphReadResponse> ReadGovernedLoopGraphAsync(
        string graphId,
        CancellationToken cancellationToken)
    {
        var runtime = await BeginCustomRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.GovernedLoopGraphAuthoring.ReadAsync(graphId, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    internal async Task<GovernedLoopGraphMutationResponse> MutateGovernedLoopGraphAsync(
        GovernedLoopGraphMutationInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var runtime = await BeginGraphAuthoringRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.GovernedLoopGraphAuthoring.MutateAsync(input, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>Prepares one selected published graph revision for visible browser invocation without accepting authority assertions.</summary>
    /// <param name="request">The browser-selected graph and revision identifiers.</param>
    /// <param name="cancellationToken">The token used before any durable authority confirmation begins.</param>
    /// <returns>Server-derived current publication, eligible exact grants, or one confirmation preview.</returns>
    internal async Task<GovernedLoopInvocationPreparationResponse> PrepareGovernedLoopInvocationAsync(
        GovernedLoopInvocationPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runtime = await BeginGraphAuthoringRuntimeOperationAsync(cancellationToken);
        try
        {
            return await runtime.PrepareGovernedLoopInvocationAsync(request, cancellationToken);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>Refreshes graph-authoring capability evidence after an applied capability lifecycle mutation.</summary>
    /// <remarks>
    /// The pinned runtime owns one graph catalog whose executable projection re-reads the shared capability lifecycle
    /// store and current Command Action isolation evidence for every graph-authoring read and mutation validation. An
    /// unpinned runtime is still retired so later requests cannot reuse other stale runtime state; a process-pinned
    /// background runtime remains alive until hosted shutdown without creating a replacement runtime.
    /// </remarks>
    internal Task InvalidateRuntimeAfterCapabilityLifecycleMutationAsync()
        => DiscardRuntimeAsync();

    private void BeginNewGovernedLoopLocalBackgroundLifetime()
    {
        lock (_backgroundStopGate)
        {
            var generation = Interlocked.Increment(ref _governedLoopBackgroundGeneration);
            _backgroundStopGeneration = generation;
            _backgroundStopCompletion = null;
            _backgroundStopReleaseTask = null;
            _lastBackgroundStopResult = null;
            Volatile.Write(ref _governedLoopBackgroundLivePeerStandby, 0);
        }
    }

    private bool MatchesBackgroundGeneration(long? expectedGeneration)
        => expectedGeneration is null
            || (expectedGeneration.Value == Volatile.Read(ref _governedLoopBackgroundGeneration)
                && expectedGeneration.Value == Volatile.Read(ref _backgroundStopGeneration));

    private void RememberBackgroundStop(
        AgentRuntime? runtime,
        AgentRuntimeGovernedLoopBackgroundStopResult result,
        long generation)
    {
        lock (_backgroundStopGate)
        {
            if (!MatchesBackgroundGeneration(generation))
            {
                return;
            }

            _backgroundStopGeneration = generation;
            _lastBackgroundStopResult = result;
            _backgroundStopCompletion = runtime is null || result.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Stopped
                ? null
                : runtime.WaitForGovernedLoopLocalBackgroundStopAsync();
        }
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
            await EnsureLoopRecoveryAsync(cancellationToken);
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
                        if (Volatile.Read(ref _loopRecoveryInProgress) != 0)
                        {
                            throw new InvalidOperationException("custom_loop_recovery_pending: durable custom-loop recovery owns the retained runtime boundary; retry the transcript request after recovery completes.");
                        }

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
                return WebStatusFactory.Create(_options, status, "already-initialized", GovernedLoopBackgroundPosture);
            }

            await _workspaceInitializer.InitializeAsync(status.RootPath, cancellationToken);
            var initializedStatus = _statusReader.Read(_options.WorkingDirectory);
            var outcome = initializedStatus.IsInitialized ? "initialized" : initializedStatus.HasPartialScaffold ? "partial" : "failed";
            return WebStatusFactory.Create(_options, initializedStatus, outcome, GovernedLoopBackgroundPosture);
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

    /// <summary>Invokes one exact published governed-loop revision under one live browser connection's approval ownership.</summary>
    /// <param name="input">The immutable publication, authority-grant, operation, and prompt coordinates.</param>
    /// <param name="ownerConnectionId">The live SignalR connection that owns governed approvals.</param>
    /// <param name="cancellationToken">The caller token linked with host shutdown for runtime acquisition and pre-boundary work.</param>
    /// <returns>The canonical governed admission, execution, replay, or recovery-required response.</returns>
    public async Task<GovernedLoopRunInvocationResponse> InvokeGovernedLoopAsync(GovernedLoopRunInvocationInput input, string ownerConnectionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var runtime = await BeginCustomRuntimeOperationAsync(executionCancellation.Token);
        try
        {
            return await runtime.InvokeGovernedLoopAsync(input, executionCancellation.Token);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    /// <summary>Confirms a server preview when required and invokes using only the exact server-returned grant.</summary>
    /// <param name="request">The browser-held object selector, preview hash, operation identity, and Manual Trigger prompt.</param>
    /// <param name="ownerConnectionId">The live SignalR connection that owns any governed approval interaction.</param>
    /// <param name="cancellationToken">The token used until durable authority or invocation boundaries are reached.</param>
    /// <returns>The canonical invocation projection, or a fail-closed safe rejection.</returns>
    public async Task<GovernedLoopRunInvocationResponse> ConfirmAndInvokeGovernedLoopAsync(
        GovernedLoopVisibleInvocationRequest request,
        string ownerConnectionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerConnectionId);

        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        using var approvalScope = _approvalCoordinator.BeginApprovalScope(ownerConnectionId);
        var runtime = await BeginGraphAuthoringRuntimeOperationAsync(executionCancellation.Token);
        try
        {
            var preparationRequest = new GovernedLoopInvocationPreparationRequest(request.GraphId, request.RevisionId);
            var preparation = await runtime.PrepareGovernedLoopInvocationAsync(preparationRequest, executionCancellation.Token);
            if (preparation.Status == GovernedLoopInvocationPreparationStatus.ConfirmationRequired)
            {
                if (request.GrantSelection is not null)
                {
                    return VisibleInvocationRejected("Invalid", "A server confirmation creates the exact least-authority grant and cannot accept an existing grant selection.");
                }
                if (string.IsNullOrWhiteSpace(request.PreviewHash))
                {
                    return VisibleInvocationRejected("ConfirmationRequired", "Explicit confirmation of the current server preview is required before this graph can run.");
                }

                return await ConfirmPreviewAndInvokeAsync(runtime, request, preparationRequest, executionCancellation.Token);
            }

            if (preparation.Status != GovernedLoopInvocationPreparationStatus.Ready || preparation.Publication is null)
            {
                return VisibleInvocationPreparationResult(preparation);
            }
            if (!string.IsNullOrWhiteSpace(request.PreviewHash))
            {
                if (request.GrantSelection is not null)
                {
                    return VisibleInvocationRejected("Invalid", "A preview confirmation cannot also select an existing authority grant.");
                }

                return await ConfirmPreviewAndInvokeAsync(runtime, request, preparationRequest, executionCancellation.Token);
            }
            if (request.GrantSelection is null)
            {
                return VisibleInvocationRejected("GrantChoiceRequired", "Select one current exact authority grant before invoking.");
            }
            var selectedChoices = preparation.EligibleGrants
                .Where(choice => string.Equals(choice.Grant.GrantId.Value, request.GrantSelection.GrantId, StringComparison.Ordinal)
                    && choice.Grant.Revision.Value == request.GrantSelection.Revision
                    && string.Equals(choice.Grant.ContentHash, request.GrantSelection.ContentHash, StringComparison.Ordinal))
                .ToArray();
            if (selectedChoices.Length != 1)
            {
                return VisibleInvocationRejected("Stale", "The selected exact authority grant is no longer eligible for the current publication. Prepare again before invoking.");
            }

            return await runtime.InvokeGovernedLoopAsync(
                new GovernedLoopRunInvocationInput(request.OperationId, preparation.Publication, selectedChoices[0].Grant, request.InvocationPrompt, IncludeInvokingConversation: false),
                executionCancellation.Token);
        }
        finally
        {
            await EndCustomRuntimeOperationAsync();
        }
    }

    private static async Task<GovernedLoopRunInvocationResponse> ConfirmPreviewAndInvokeAsync(
        AgentRuntime runtime,
        GovernedLoopVisibleInvocationRequest request,
        GovernedLoopInvocationPreparationRequest preparationRequest,
        CancellationToken cancellationToken)
    {
        var confirmation = await runtime.ConfirmGovernedLoopInvocationAuthorityAsync(
            new GovernedLoopInvocationAuthorityConfirmation(request.GraphId, request.RevisionId, request.PreviewHash!, request.OperationId),
            cancellationToken);
        if (confirmation.Status != GovernedLoopInvocationAuthorityConfirmationStatus.Confirmed || confirmation.Grant is null)
        {
            return VisibleInvocationConfirmationResult(confirmation);
        }

        var preparation = await runtime.PrepareGovernedLoopInvocationAsync(preparationRequest, cancellationToken);
        var confirmedChoice = preparation.EligibleGrants.SingleOrDefault(choice => choice.Grant.Equals(confirmation.Grant));
        if (preparation.Status != GovernedLoopInvocationPreparationStatus.Ready || preparation.Publication is null || confirmedChoice is null)
        {
            return VisibleInvocationRejected("Stale", "The confirmed authority no longer matches the current exact publication. Prepare and confirm again before invoking.");
        }

        return await runtime.InvokeGovernedLoopAsync(
            new GovernedLoopRunInvocationInput(request.OperationId, preparation.Publication, confirmedChoice.Grant, request.InvocationPrompt, IncludeInvokingConversation: false),
            cancellationToken);
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
    /// Static commands are handled before workspace initialization is required. A cancelled model turn before its
    /// irreversible provider transport-write boundary marks an unpinned runtime for disposal so its session is not
    /// reused after an ambiguous cancellation boundary. A process-pinned runtime retains its complete background
    /// composition but quarantines only the default-conversation provider transport/session. Once that boundary has been
    /// crossed, transport failures quarantine the provider attempt for review without claiming that runtime disposal can undo the dispatched request.
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

        using var turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _hostLifetimeCancellation.Token);
        await _turnGate.WaitAsync(turnCancellation.Token).ConfigureAwait(false);
        if (_hostLifetimeCancellation.IsCancellationRequested)
        {
            _turnGate.Release();
            throw new OperationCanceledException(turnCancellation.Token);
        }

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
            try
            {
                ClearTurnCancellation(turnCancellation);
                if (discardRuntime)
                {
                    await DiscardRuntimeAsync(quarantinePinnedDefaultConversation: true);
                }
            }
            finally
            {
                _turnGate.Release();
            }
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
    /// Cancels host-owned invocation and resume work, waits for active turns and custom operations, and disposes retained state.
    /// </summary>
    /// <returns>A value task that completes after runtime and run-inspection resources are disposed.</returns>
    /// <remarks>
    /// Disposal is idempotent. The host signal rejects new conversation turns and cancels an admitted default turn.
    /// Ordinarily disposal waits for the conversation gate before stopping or releasing the pinned background runtime.
    /// When the hosted shutdown deadline already elapsed, disposal schedules that same safe-boundary cleanup and returns
    /// so the application container can finish its bounded shutdown path without disposing synchronization resources
    /// still used by the deferred cleanup.
    /// </remarks>
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        SignalHostShutdown();
        Task disposal;
        lock (_hostDisposalGate)
        {
            disposal = _hostDisposalTask ??= DisposeAtSafeBoundaryAsync();
        }

        if (Volatile.Read(ref _hostShutdownDeadlineElapsed) != 0)
        {
            _ = ObserveDeferredHostDisposalAsync(disposal);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(disposal);
    }

    private async Task DisposeAtSafeBoundaryAsync()
    {
        await WaitForConversationTurnsAsync().ConfigureAwait(false);
        var stop = await StopGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
        SetGovernedLoopBackgroundPosture(stop.Readiness switch
        {
            AgentRuntimeGovernedLoopBackgroundReadiness.Ready => WebGovernedLoopBackgroundPosture.Ready,
            AgentRuntimeGovernedLoopBackgroundReadiness.Degraded => WebGovernedLoopBackgroundPosture.Degraded,
            AgentRuntimeGovernedLoopBackgroundReadiness.Draining => WebGovernedLoopBackgroundPosture.Draining,
            AgentRuntimeGovernedLoopBackgroundReadiness.Stopped => WebGovernedLoopBackgroundPosture.Stopped,
            _ => WebGovernedLoopBackgroundPosture.Unavailable
        });
        if (stop.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Draining
            || Volatile.Read(ref _backgroundStopCompletion) is not null)
        {
            await BeginGovernedLoopLocalBackgroundStopReleaseAsync().ConfigureAwait(false);
        }
        else if (stop.Readiness != AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable)
        {
            await ReleaseGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
        }
        await DiscardRuntimeAsync(waitForCustomOperations: true);
        await WaitForAuthoringOperationsAsync();
        await WaitForGovernedLoopLocalBackgroundLifetimeDrainAsync();
        await _customLoopRunStoreProvider.DisposeAsync();
        await _loopRuns.DisposeAsync();
        _runtimeGate.Dispose();
        _backgroundLifetimeGate.Dispose();
        _authoringOperationGate.Dispose();
        _turnGate.Dispose();
        _workspaceInitializationGate.Dispose();
        _hostLifetimeCancellation.Dispose();
    }

    private async Task ObserveDeferredHostDisposalAsync(Task disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch
        {
            // The caller has already received its bounded shutdown result. Preserve fail-closed posture while the
            // process lifetime owns any synchronization resources that a failed safe-boundary cleanup could still use.
            SetGovernedLoopBackgroundPosture(WebGovernedLoopBackgroundPosture.Unavailable);
        }
    }

    private async Task WaitForGovernedLoopLocalBackgroundLifetimeDrainAsync()
    {
        Task? drainTask;
        lock (_backgroundStopGate)
        {
            drainTask = _backgroundLifetimeDrainTask;
        }

        if (drainTask is not null)
        {
            await drainTask.ConfigureAwait(false);
        }
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

    private async Task BeginAuthoringOperationAsync(CancellationToken cancellationToken)
    {
        await _authoringOperationGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _activeAuthoringOperations++;
        }
        finally
        {
            _authoringOperationGate.Release();
        }
    }

    private async Task EndAuthoringOperationAsync()
    {
        await _authoringOperationGate.WaitAsync(CancellationToken.None);
        try
        {
            _activeAuthoringOperations--;
            if (_activeAuthoringOperations == 0)
            {
                _authoringOperationDrainCompletion?.TrySetResult(true);
            }
        }
        finally
        {
            _authoringOperationGate.Release();
        }
    }

    private async Task WaitForAuthoringOperationsAsync()
    {
        Task? drainCompletion = null;
        await _authoringOperationGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_activeAuthoringOperations > 0)
            {
                _authoringOperationDrainCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                drainCompletion = _authoringOperationDrainCompletion.Task;
            }
        }
        finally
        {
            _authoringOperationGate.Release();
        }

        if (drainCompletion is not null)
        {
            await drainCompletion;
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

    private async Task<AgentRuntime> BeginGraphAuthoringRuntimeOperationAsync(CancellationToken cancellationToken)
    {
        EnsureWorkspaceInitialized("authoring governed graphs");

        while (true)
        {
            Task? discardCompletion = null;
            await _runtimeGate.WaitAsync(cancellationToken);
            try
            {
                if (!_discardRuntimeWhenCustomOperationsComplete)
                {
                    var runtime = await GetOrCreateRuntimeUnderGateAsync(cancellationToken);
                    _activeCustomRuntimeOperations++;
                    return runtime;
                }

                discardCompletion = _runtimeDiscardCompletion?.Task;
            }
            finally
            {
                _runtimeGate.Release();
            }

            await (discardCompletion ?? throw new InvalidOperationException("The pending runtime discard did not retain a completion boundary.")).WaitAsync(cancellationToken);
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
        _hostLifetimeCancellation.Token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _loopRecoveryInProgress) != 0)
        {
            throw new InvalidOperationException("custom_loop_recovery_pending: durable custom-loop recovery owns the retained runtime boundary; retry the request after recovery completes.");
        }

        if (_runtime is null)
        {
            var codexRuntimeStatus = await GetCodexRuntimeStatusAsync(cancellationToken);
            if (codexRuntimeStatus.Compatibility != CodexRuntimeCompatibility.Compatible)
            {
                throw new CodexRuntimeUnavailableException(codexRuntimeStatus);
            }

            var factory = (_runtimeFactoryProvider?.Invoke(codexRuntimeStatus)
                ?? (_capabilityTrustRootPath is null
                    ? _conversationPublicationObserver is null
                        ? new AgentRuntimeFactory(_approvalCoordinator, codexRuntimeStatus)
                        : new AgentRuntimeFactory(_approvalCoordinator, _conversationPublicationObserver, codexRuntimeStatus)
                    : AgentRuntimeFactory.ForFileCapabilityTrustRoot(
                        _approvalCoordinator,
                        _capabilityTrustRootPath,
                        codexRuntimeStatus,
                        _conversationPublicationObserver)))
                .WithCustomLoopRunStoreProvider(_customLoopRunStoreProvider);
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
        var releasePinnedRuntime = false;
        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            if (Volatile.Read(ref _loopRecoveryInProgress) != 0)
            {
                throw new InvalidOperationException("custom_loop_recovery_pending: another recovery owns the retained runtime boundary; retry the evidence request afterward.");
            }

            if (_runtime?.CustomLoopRecoveryRequired == true && _governedLoopBackgroundRuntimePinned)
            {
                if (_activeCustomRuntimeOperations > 0)
                {
                    throw new InvalidOperationException("custom_loop_recovery_pending: the retained background runtime still has an active custom-loop operation; retry recovery after its safe boundary.");
                }

                _loopRecoveryCompleted = false;
                Volatile.Write(ref _loopRecoveryInProgress, 1);
                releasePinnedRuntime = true;
            }
            else
            {
                Volatile.Write(ref _loopRecoveryInProgress, 1);
                try
                {
                    await EnsureLoopRecoveryUnderGateAsync(cancellationToken);
                }
                finally
                {
                    Volatile.Write(ref _loopRecoveryInProgress, 0);
                }
            }
        }
        finally
        {
            _runtimeGate.Release();
        }

        if (!releasePinnedRuntime)
        {
            return;
        }

        try
        {
            var stop = await StopGovernedLoopLocalBackgroundForProcessAsync().ConfigureAwait(false);
            if (stop.Readiness != AgentRuntimeGovernedLoopBackgroundReadiness.Stopped)
            {
                throw new InvalidOperationException("custom_loop_recovery_pending: the pinned governed-loop background runtime has not reached a terminal stop boundary; retry the evidence request afterward.");
            }

            // Retain the exact stopped coordinator while its run-store recovery completes. Its confirmed terminal
            // ownership permits the Web lifetime to restart immediately afterward without mistaking its own unexpired
            // 30-second heartbeat for a live peer; the recovery flag blocks every other runtime use meanwhile.
            await ReleaseGovernedLoopLocalBackgroundForProcessAsync(retainRuntime: true).ConfigureAwait(false);
            await _runtimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RecoverLoopRunStoresUnderGateAsync(cancellationToken);
            }
            finally
            {
                _runtimeGate.Release();
            }
        }
        finally
        {
            Volatile.Write(ref _loopRecoveryInProgress, 0);
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
            if (_governedLoopBackgroundRuntimePinned)
            {
                throw new InvalidOperationException("custom_loop_recovery_pending: the retained background runtime must reach a terminal release boundary before durable recovery can replace shared stores.");
            }

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

        await RecoverLoopRunStoresUnderGateAsync(cancellationToken);
    }

    private async Task RecoverLoopRunStoresUnderGateAsync(CancellationToken cancellationToken)
    {
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

    private static string RequireTrustRootPath(string capabilityTrustRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityTrustRootPath);
        return Path.GetFullPath(capabilityTrustRootPath);
    }

    private async Task DiscardRuntimeAsync(bool waitForCustomOperations = false, bool quarantinePinnedDefaultConversation = false)
    {
        Task? discardCompletion = null;
        await _runtimeGate.WaitAsync(CancellationToken.None);
        try
        {
            if (_governedLoopBackgroundRuntimePinned)
            {
                if (Volatile.Read(ref _loopRecoveryInProgress) != 0)
                {
                    return;
                }

                if (quarantinePinnedDefaultConversation && _runtime is not null)
                {
                    await _runtime.QuarantineDefaultConversationProviderAsync().ConfigureAwait(false);
                }

                return;
            }

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

    private static GovernedLoopRunInvocationResponse VisibleInvocationRejected(string failureCode, string detail)
    {
        return new GovernedLoopRunInvocationResponse("Rejected", "Rejected", failureCode, null, null, false, null, null, detail);
    }

    private static GovernedLoopRunInvocationResponse VisibleInvocationConfirmationResult(
        GovernedLoopInvocationAuthorityConfirmationResult confirmation)
    {
        if (confirmation.Status == GovernedLoopInvocationAuthorityConfirmationStatus.Unavailable)
        {
            return new GovernedLoopRunInvocationResponse("Unavailable", null, null, null, null, false, null, null, confirmation.Detail);
        }

        return VisibleInvocationRejected(confirmation.Status.ToString(), confirmation.Detail);
    }

    private static GovernedLoopRunInvocationResponse VisibleInvocationPreparationResult(
        GovernedLoopInvocationPreparationResponse preparation)
    {
        if (preparation.Status == GovernedLoopInvocationPreparationStatus.Unavailable)
        {
            return new GovernedLoopRunInvocationResponse("Unavailable", null, null, null, null, false, null, null, preparation.Detail);
        }

        return VisibleInvocationRejected(preparation.Status.ToString(), preparation.Detail);
    }

    private static WebGovernedLoopBackgroundPosture ToWebPosture(
        AgentRuntimeGovernedLoopBackgroundReadiness readiness)
        => readiness switch
        {
            AgentRuntimeGovernedLoopBackgroundReadiness.Ready => WebGovernedLoopBackgroundPosture.Ready,
            AgentRuntimeGovernedLoopBackgroundReadiness.Degraded => WebGovernedLoopBackgroundPosture.Degraded,
            AgentRuntimeGovernedLoopBackgroundReadiness.Draining => WebGovernedLoopBackgroundPosture.Draining,
            AgentRuntimeGovernedLoopBackgroundReadiness.Stopped => WebGovernedLoopBackgroundPosture.Stopped,
            _ => WebGovernedLoopBackgroundPosture.Unavailable
        };

    private static WebGovernedLoopBackgroundPosture ToWebReleasePosture(
        AgentRuntimeGovernedLoopBackgroundStopResult stop,
        bool wasLivePeerStandby)
        => (wasLivePeerStandby || stop.Status == AgentRuntimeGovernedLoopBackgroundStopStatus.OwnershipLost)
            && stop.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Degraded
            && stop.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer
            ? WebGovernedLoopBackgroundPosture.Stopped
            : ToWebPosture(stop.Readiness);

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
