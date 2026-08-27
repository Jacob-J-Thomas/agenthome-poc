using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Sleep;
using EmbodySense.Core.Application.Loops.Sleep.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Loops.Execution.Sleep;
using EmbodySense.Core.Startup.Loops.Execution.Sleep.Models;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Application.Triggers.Schedules;
using EmbodySense.Core.Persistence.Loops;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Startup.Triggers;
using EmbodySense.Core.Startup.Loops.Posture;
using EmbodySense.Core.Startup.Loops.GraphAuthoring;
using EmbodySense.Core.Startup.Inference.Profiles;
using EmbodySense.Core.Startup.Loops.InvocationPreparation;
using EmbodySense.Core.Startup.Loops.InvocationPreparation.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Exposes one composed EmbodySense conversation runtime through the interface-safe Core.Startup boundary.
/// </summary>
/// <remarks>
/// The runtime owns its inference client. Its canonical custom-loop run store is either owned directly or borrowed from a
/// longer-lived workspace host, and its custom-loop and authoring facades borrow that same store. Callers must dispose the
/// instance to release its app-server process, cancellation host, and workspace execution-gate resources. Once the default loop accepts a model turn, cancellation,
/// provider transport, streamed-callback, audit, and persistence failures are normally projected through
/// <see cref="AgentRuntimeTurnResult"/> rather than thrown. Input validation, command handling, and failures before loop admission
/// can still propagate to the caller.
/// </remarks>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IAsyncDisposable _inferenceClient;
    private readonly IDefaultConversationLoopRunner _loopRunner;
    private readonly RuntimeSessionState _state = new();
    private readonly RuntimeCommandService _commandService;
    private readonly ConversationRuntimeState _conversationState;
    private readonly CustomLoopRunStore _customRunStore;
    private readonly bool _ownsCustomRunStore;
    private readonly CustomLoopRuntimeFacade _customLoops;
    private readonly LoopAuthoringFacade _loopAuthoring;
    private readonly GovernedLoopRuntimeFacade _governedLoops;
    private readonly IScheduleDeliveryProvenancePort _scheduleDeliveryProvenance;
    private readonly GovernedLoopOperationalFacade _governedLoopOperations;
    private readonly GovernedLoopGraphAuthoringFacade _governedLoopGraphAuthoring;
    private readonly GovernedLoopInvocationPreparationFacade _governedLoopInvocationPreparation;
    private readonly IModelProfileCatalogFacade _modelProfiles;
    private readonly DefaultConversationTurnReviewService _defaultConversationReviews;
    private readonly ITriggerWorkerCurrentEvidenceAuthorizer _triggerWorkerCurrentEvidenceAuthorizer;
    private readonly GovernedLoopBackgroundRuntimeHost _governedBackgroundRuntimeHost;
    private readonly GovernedLoopSleepService? _governedSleep;
    private TaskCompletionSource<bool>? _disposeCompletion;
    private int _disposed;

    internal AgentRuntime(
        WorkspacePaths paths,
        AgentRuntimeSurface surface,
        IConversationMemoryStore conversationMemory,
        IReadOnlyList<LlmMessage> startupContext,
        ConversationRuntimeState conversationState,
        IAsyncDisposable inferenceClient,
        IDefaultConversationLoopRunner loopRunner,
        CustomLoopRunStore customRunStore,
        bool ownsCustomRunStore,
        CustomLoopRuntimeFacade customLoops,
        LoopAuthoringFacade loopAuthoring,
        GovernedLoopRuntimeFacade governedLoops,
        IScheduleDeliveryProvenancePort scheduleDeliveryProvenance,
        GovernedLoopOperationalFacade governedLoopOperations,
        GovernedLoopGraphAuthoringFacade governedLoopGraphAuthoring,
        GovernedLoopInvocationPreparationFacade governedLoopInvocationPreparation,
        IModelProfileCatalogFacade modelProfiles,
        DefaultConversationTurnReviewService defaultConversationReviews,
        CodexRuntimeStatus codexRuntimeStatus,
        ITriggerWorkerCurrentEvidenceAuthorizer triggerWorkerCurrentEvidenceAuthorizer,
        GovernedLoopBackgroundRuntimeHost governedBackgroundRuntimeHost,
        GovernedLoopSleepService? governedSleep = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(startupContext);
        ArgumentNullException.ThrowIfNull(conversationState);
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(customRunStore);
        ArgumentNullException.ThrowIfNull(customLoops);
        ArgumentNullException.ThrowIfNull(loopAuthoring);
        ArgumentNullException.ThrowIfNull(governedLoops);
        ArgumentNullException.ThrowIfNull(governedLoopOperations);
        ArgumentNullException.ThrowIfNull(governedLoopGraphAuthoring);
        ArgumentNullException.ThrowIfNull(governedLoopInvocationPreparation);
        ArgumentNullException.ThrowIfNull(modelProfiles);
        ArgumentNullException.ThrowIfNull(defaultConversationReviews);
        ArgumentNullException.ThrowIfNull(triggerWorkerCurrentEvidenceAuthorizer);
        ArgumentNullException.ThrowIfNull(governedBackgroundRuntimeHost);
        ArgumentNullException.ThrowIfNull(codexRuntimeStatus);

        Paths = paths;
        Surface = surface;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        _conversationState = conversationState;
        _inferenceClient = inferenceClient;
        _loopRunner = loopRunner;
        _customRunStore = customRunStore;
        _ownsCustomRunStore = ownsCustomRunStore;
        _customLoops = customLoops;
        _loopAuthoring = loopAuthoring;
        _governedLoops = governedLoops;
        _scheduleDeliveryProvenance = scheduleDeliveryProvenance ?? throw new ArgumentNullException(nameof(scheduleDeliveryProvenance));
        _governedLoopOperations = governedLoopOperations ?? throw new ArgumentNullException(nameof(governedLoopOperations));
        _governedLoopGraphAuthoring = governedLoopGraphAuthoring;
        _governedLoopInvocationPreparation = governedLoopInvocationPreparation;
        _modelProfiles = modelProfiles;
        _defaultConversationReviews = defaultConversationReviews;
        _triggerWorkerCurrentEvidenceAuthorizer = triggerWorkerCurrentEvidenceAuthorizer;
        _governedBackgroundRuntimeHost = governedBackgroundRuntimeHost;
        _governedSleep = governedSleep;
        _commandService = new RuntimeCommandService(conversationMemory, startupContext);
        CodexRuntimeStatus = codexRuntimeStatus;
    }

    internal WorkspacePaths Paths { get; }

    /// <summary>
    /// Gets the interface surface whose identity is used for runtime attribution and audit records.
    /// </summary>
    public AgentRuntimeSurface Surface { get; }

    /// <summary>
    /// Gets the compatible Codex executable and model resolution used to create this runtime.
    /// </summary>
    public CodexRuntimeStatus CodexRuntimeStatus { get; }

    /// <summary>
    /// Gets a value indicating whether custom-loop execution remains disabled pending explicit persistence cleanup or recovery.
    /// </summary>
    public bool CustomLoopRecoveryRequired => _customLoops.CustomRecoveryRequired;

    /// <summary>Gets the shared typed posture and lifecycle-control facade over this runtime's canonical stores.</summary>
    public GovernedLoopOperationalFacade GovernedLoopOperations => _governedLoopOperations;

    /// <summary>Gets the authoring facade backed by this runtime's canonical custom-loop run store.</summary>
    /// <remarks>
    /// The facade never owns its borrowed store. Callers retaining authoring past runtime disposal must use the longer-lived
    /// <see cref="CustomLoopRunStoreProvider"/> facade that supplied the store, when runtime composition was configured with one.
    /// </remarks>
    public LoopAuthoringFacade LoopAuthoring => _loopAuthoring;

    /// <summary>Gets the shared catalog, immutable graph history, and role-bound lifecycle authoring facade.</summary>
    public GovernedLoopGraphAuthoringFacade GovernedLoopGraphAuthoring => _governedLoopGraphAuthoring;

    /// <summary>Gets the server-derived current-publication preparation and confirmation facade for visible governed invocation.</summary>
    public GovernedLoopInvocationPreparationFacade GovernedLoopInvocationPreparation => _governedLoopInvocationPreparation;

    /// <summary>Gets the shared safe model-profile catalog and exact configured default.</summary>
    public IModelProfileCatalogFacade ModelProfiles => _modelProfiles;

    internal IConversationMemoryStore ConversationMemory { get; }

    internal IReadOnlyList<LlmMessage> StartupContext { get; }

    internal IReadOnlyList<LlmMessage> Messages => _conversationState.Messages;

    /// <summary>
    /// Projects the restored and session-authored messages that belong to the active conversation.
    /// </summary>
    /// <returns>A detached, ordered transcript projection that excludes startup-only context.</returns>
    public IReadOnlyList<AgentRuntimeTranscriptMessage> GetActiveConversationTranscript()
    {
        return _conversationState.ContextMessages
            .Where(message => message.Source is RuntimeContextSource.RestoredConversationHistory or RuntimeContextSource.SessionTranscript)
            .Select(message => new AgentRuntimeTranscriptMessage(message.Message.Role.ToString(), message.Message.Content))
            .ToArray();
    }

    /// <summary>
    /// Handles a runtime command or executes one default-conversation model turn.
    /// </summary>
    /// <remarks>
    /// The owning host must serialize calls to this member. Runtime commands retain session-scoped pending-history interaction
    /// state that is not protected by the durable model-turn conversation lease, so interleaved command and model calls can
    /// otherwise consume or clear another call's pending interaction.
    /// </remarks>
    /// <param name="input">The command or user message to process.</param>
    /// <param name="responseChunkHandler">An optional callback for streamed assistant-message deltas.</param>
    /// <param name="verboseContextHandler">An optional callback for verbose context diagnostics when verbose mode is enabled.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="requestId">An optional caller-owned idempotency identity for model turns.</param>
    /// <returns>A task whose result projects command output, transcript events, completion, cancellation, or failure.</returns>
    public async Task<AgentRuntimeTurnResult> RunTurnAsync(
        string input,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<string, CancellationToken, Task>? verboseContextHandler = null,
        CancellationToken cancellationToken = default,
        string? requestId = null)
    {
        var reviewCommand = await TryHandleDefaultConversationReviewCommandAsync(input, cancellationToken);
        if (reviewCommand is not null)
        {
            return reviewCommand;
        }

        var commandResult = await _commandService.TryHandleAsync(input, _conversationState, _state, cancellationToken);
        if (commandResult.Handled)
        {
            return AgentRuntimeTurnResultFactory.FromCommand(commandResult);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return await RunModelTurnAsync(input, responseChunkHandler, verboseContextHandler, cancellationToken, requestId);
    }

    /// <summary>Quarantines only the live default-conversation provider transport while retaining this runtime composition.</summary>
    /// <remarks>
    /// This operation is used when a process-pinned runtime must retire an ambiguous default-conversation session without
    /// disposing its coordinator, stores, or governed-loop dependencies. The next default model turn creates a fresh
    /// provider transport through the same runtime. Production runtime composition supplies a quarantinable provider.
    /// </remarks>
    /// <param name="cancellationToken">The token used while the provider transport is retired.</param>
    /// <returns>A task that completes after the provider transport is quarantined.</returns>
    /// <exception cref="InvalidOperationException">The composed provider does not support transport quarantine.</exception>
    public Task QuarantineDefaultConversationProviderAsync(CancellationToken cancellationToken = default)
    {
        if (_inferenceClient is not IQuarantinableInferenceClient quarantinableClient)
        {
            throw new InvalidOperationException("The composed default-conversation provider cannot be quarantined.");
        }

        return quarantinableClient.QuarantineAsync(cancellationToken);
    }

    /// <summary>
    /// Lists unresolved default-conversation review evidence for CLI and Web projections.
    /// </summary>
    public async Task<IReadOnlyList<DefaultConversationReviewSnapshot>> ListDefaultConversationReviewsAsync(CancellationToken cancellationToken = default)
    {
        var records = await _defaultConversationReviews.ListAsync(cancellationToken);
        return records.Select(ToReviewSnapshot).ToArray();
    }

    /// <summary>
    /// Explicitly abandons one inspected outcome-unknown attempt after quarantining provider transport.
    /// </summary>
    public async Task<DefaultConversationReviewSnapshot?> ResolveDefaultConversationReviewAsync(string turnId, CancellationToken cancellationToken = default)
    {
        var record = await _defaultConversationReviews.ResolveAsync(turnId, cancellationToken);
        return record is null ? null : ToReviewSnapshot(record);
    }

    private async Task<AgentRuntimeTurnResult?> TryHandleDefaultConversationReviewCommandAsync(string input, CancellationToken cancellationToken)
    {
        if (string.Equals(input?.Trim(), "/review", StringComparison.OrdinalIgnoreCase))
        {
            var reviews = await ListDefaultConversationReviewsAsync(cancellationToken);
            if (reviews.Count == 0)
            {
                return AgentRuntimeTurnResult.CommandOutput("No unresolved default-conversation reviews were found.");
            }

            var lines = reviews.Select(review => $"- {review.TurnId}: {review.Classification}; attempt `{review.ProviderAttemptId}`, correlation `{review.ProviderCorrelationId}` - {review.Detail} Allowed action: {review.AllowedAction}");
            return AgentRuntimeTurnResult.CommandOutput("Unresolved default-conversation reviews:" + Environment.NewLine + string.Join(Environment.NewLine, lines));
        }

        const string ResolvePrefix = "/review resolve ";
        if (input is null || !input.TrimStart().StartsWith(ResolvePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var turnId = input.Trim()[ResolvePrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return AgentRuntimeTurnResult.CommandOutput("Usage: /review resolve <turn-id>");
        }

        var review = (await ListDefaultConversationReviewsAsync(cancellationToken)).SingleOrDefault(candidate => string.Equals(candidate.TurnId, turnId, StringComparison.Ordinal));
        if (review is not null && review.Classification != DefaultConversationTurnReviewClassification.OutcomeUnknown)
        {
            return AgentRuntimeTurnResult.CommandOutput($"Default-conversation turn `{turnId}` is classified as {review.Classification} and cannot be abandoned. {review.AllowedAction}");
        }

        var resolved = await ResolveDefaultConversationReviewAsync(turnId, cancellationToken);
        return resolved is null
            ? AgentRuntimeTurnResult.CommandOutput($"Default-conversation turn `{turnId}` was not found.")
            : AgentRuntimeTurnResult.CommandOutput($"Resolved `{turnId}` by explicitly abandoning its outcome-unknown provider attempt. No provider request or transcript publication was replayed.");
    }

    private static DefaultConversationReviewSnapshot ToReviewSnapshot(DefaultConversationTurnRecord record)
    {
        return new DefaultConversationReviewSnapshot(
            record.TurnId,
            record.RequestId,
            record.Run.RunId,
            record.LifecycleVersion,
            record.ProviderAttemptId,
            record.ProviderCorrelationId,
            record.ReviewDetail ?? "No review detail was retained.",
            DefaultConversationTurnProtocol.GetReviewClassification(record),
            DefaultConversationTurnProtocol.GetReviewAction(record));
    }

    /// <summary>
    /// Enables or disables verbose runtime-context diagnostics for subsequent model turns.
    /// </summary>
    /// <param name="enabled">Whether verbose diagnostics should be emitted.</param>
    /// <returns>A command-style result describing the new verbose state.</returns>
    public AgentRuntimeTurnResult SetVerbose(bool enabled)
    {
        _state.SetVerbose(enabled);
        return AgentRuntimeTurnResult.CommandOutput(enabled ? RuntimeCommandOutput.VerboseEnabledText : "Verbose mode disabled.");
    }

    /// <summary>
    /// Admits and synchronously invokes one published custom-loop definition.
    /// </summary>
    /// <param name="input">The immutable definition and operation identity used for admission. The canonical trigger-worker operation namespace is reserved for authenticated trigger dispatch.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the durable admission receipt and projected run evidence.</returns>
    public Task<LoopRunInvocationResponse> InvokeCustomLoopAsync(LoopRunInvocationInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.InvokeAsync(input, cancellationToken);
    }

    /// <summary>Invokes one exact published governed-loop revision through canonical admission and the shared durable runtime.</summary>
    /// <param name="input">The operation, immutable publication and grant pins, and entry-trigger prompt.</param>
    /// <param name="cancellationToken">The token used until an irreversible or durable integrity boundary is reached.</param>
    /// <returns>The canonical admission, materialization, execution, replay, or recovery-required projection.</returns>
    /// <remarks>
    /// Workspace, actor, surface, role, graph payload, model, context, run identity, and execution generation are captured or
    /// derived by the runtime. The existing custom-loop API remains a separate compatibility entry point during convergence.
    /// </remarks>
    public Task<GovernedLoopRunInvocationResponse> InvokeGovernedLoopAsync(
        GovernedLoopRunInvocationInput input,
        CancellationToken cancellationToken = default)
    {
        return _governedLoops.InvokeAsync(input, cancellationToken);
    }

    /// <summary>Prepares server-authorized exact grant choices or a non-persisted least-authority confirmation preview.</summary>
    /// <param name="request">The Builder-selected graph and revision identifiers.</param>
    /// <param name="cancellationToken">The token used before a durable authority operation begins.</param>
    /// <returns>Only server-derived eligibility and exact grant-reference projections.</returns>
    public Task<GovernedLoopInvocationPreparationResponse> PrepareGovernedLoopInvocationAsync(
        GovernedLoopInvocationPreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        return _governedLoopInvocationPreparation.PrepareAsync(request, cancellationToken);
    }

    /// <summary>Confirms one exact server-derived least-authority preview and returns its durable exact grant reference.</summary>
    /// <param name="confirmation">The selected graph revision, expected server preview hash, and durable operation identity.</param>
    /// <param name="cancellationToken">The token used until a durable profile or grant boundary is reached.</param>
    /// <returns>The exact confirmed grant reference or a fail-closed result.</returns>
    public Task<GovernedLoopInvocationAuthorityConfirmationResult> ConfirmGovernedLoopInvocationAuthorityAsync(
        GovernedLoopInvocationAuthorityConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        return _governedLoopInvocationPreparation.ConfirmAsync(confirmation, cancellationToken);
    }

    /// <summary>Delivers one already-authenticated event to an exact governed Wait checkpoint.</summary>
    /// <param name="input">The exact checkpoint identity and hash plus the surface-owned authentication evidence hash.</param>
    /// <param name="cancellationToken">The token used until durable continuation intent exists.</param>
    /// <returns>The bounded durable wake outcome without exposing Application-layer contracts.</returns>
    /// <exception cref="OperationCanceledException">Thrown when cancellation is requested before durable prepared wake intent exists.</exception>
    public async Task<AgentRuntimeAuthenticatedWakeDeliveryResult> DeliverAuthenticatedWakeAsync(
        AgentRuntimeAuthenticatedWakeDeliveryInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (_governedSleep is null)
        {
            return new AgentRuntimeAuthenticatedWakeDeliveryResult(AgentRuntimeAuthenticatedWakeDeliveryStatus.Unavailable);
        }

        var result = await _governedSleep.WakeAsync(
            new GovernedLoopWakeRequest(input.CheckpointId, input.CheckpointHash, input.AuthenticationEvidenceHash),
            cancellationToken);
        return new AgentRuntimeAuthenticatedWakeDeliveryResult(
            MapAuthenticatedWakeDeliveryStatus(result.Status),
            result.Evidence?.Identity.WakeId,
            result.Evidence?.ContentHash,
            result.ContinuationInvoked);
    }

    private static AgentRuntimeAuthenticatedWakeDeliveryStatus MapAuthenticatedWakeDeliveryStatus(GovernedLoopWakeResultStatus status)
        => status switch
        {
            GovernedLoopWakeResultStatus.Committed => AgentRuntimeAuthenticatedWakeDeliveryStatus.Committed,
            GovernedLoopWakeResultStatus.Duplicate => AgentRuntimeAuthenticatedWakeDeliveryStatus.Duplicate,
            GovernedLoopWakeResultStatus.NotEligible => AgentRuntimeAuthenticatedWakeDeliveryStatus.NotEligible,
            GovernedLoopWakeResultStatus.Late => AgentRuntimeAuthenticatedWakeDeliveryStatus.Late,
            GovernedLoopWakeResultStatus.Stale => AgentRuntimeAuthenticatedWakeDeliveryStatus.Stale,
            GovernedLoopWakeResultStatus.Conflict => AgentRuntimeAuthenticatedWakeDeliveryStatus.Conflict,
            GovernedLoopWakeResultStatus.Cancelled => AgentRuntimeAuthenticatedWakeDeliveryStatus.Cancelled,
            GovernedLoopWakeResultStatus.Expired => AgentRuntimeAuthenticatedWakeDeliveryStatus.Expired,
            GovernedLoopWakeResultStatus.Paused => AgentRuntimeAuthenticatedWakeDeliveryStatus.Paused,
            GovernedLoopWakeResultStatus.ReviewBlocked => AgentRuntimeAuthenticatedWakeDeliveryStatus.ReviewBlocked,
            GovernedLoopWakeResultStatus.AmbiguousAttempt => AgentRuntimeAuthenticatedWakeDeliveryStatus.AmbiguousAttempt,
            GovernedLoopWakeResultStatus.Failed => AgentRuntimeAuthenticatedWakeDeliveryStatus.Failed,
            GovernedLoopWakeResultStatus.Invalid => AgentRuntimeAuthenticatedWakeDeliveryStatus.Invalid,
            GovernedLoopWakeResultStatus.NotFound => AgentRuntimeAuthenticatedWakeDeliveryStatus.NotFound,
            GovernedLoopWakeResultStatus.Unavailable => AgentRuntimeAuthenticatedWakeDeliveryStatus.Unavailable,
            _ => AgentRuntimeAuthenticatedWakeDeliveryStatus.Invalid,
        };

    /// <summary>Creates an explicit one-shot trigger worker bound to this runtime's governed custom-loop execution gate.</summary>
    /// <remarks>
    /// The supplied authorizer is retained as a composition-owned trusted current-state source; it is not accepted per dispatch.
    /// Creating this facade does not start background work or automatically continue interrupted dispatches.
    /// </remarks>
    /// <param name="authorizer">The trusted current loop, assignment, capability, authority, actor, workspace, and temporal evidence source.</param>
    /// <param name="timeProvider">An optional composition-owned UTC clock.</param>
    /// <returns>A facade for queue posture and explicit one-shot dispatch.</returns>
    public TriggerWorkerRuntimeFacade CreateTriggerWorkerRuntime(ITriggerWorkerCurrentEvidenceAuthorizer authorizer, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        var clock = timeProvider ?? TimeProvider.System;
        var store = new TriggerQueueStore(Paths, TriggerQueueQuota.Runtime, timeProvider: clock);
        var service = CreateTriggerWorkerService(authorizer, store, clock);
        return new TriggerWorkerRuntimeFacade(store, service);
    }

    /// <summary>Creates a one-shot trigger worker using this runtime's factory-owned current-evidence authorizer.</summary>
    /// <remarks>
    /// This facade shares the runtime's canonical dispatcher and authority sources. It is intended for an explicit process
    /// host that needs one bounded dispatch attempt without creating a second trigger composition.
    /// </remarks>
    /// <param name="timeProvider">An optional composition-owned UTC clock.</param>
    /// <returns>A one-shot trigger worker bound to the canonical runtime composition.</returns>
    public TriggerWorkerRuntimeFacade CreateCanonicalTriggerWorkerRuntime(TimeProvider? timeProvider = null)
        => CreateTriggerWorkerRuntime(_triggerWorkerCurrentEvidenceAuthorizer, timeProvider);

    private TriggerWorkerService CreateTriggerWorkerService(
        ITriggerWorkerCurrentEvidenceAuthorizer authorizer,
        ITriggerWorkerStatePort state,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(state);
        var clock = timeProvider ?? TimeProvider.System;
        return new TriggerWorkerService(
            state,
            new TriggerWorkerCurrentEvidenceAuthorizerAdapter(authorizer),
            new TriggerCustomLoopDispatcher(_customLoops, _governedLoops),
            new ScheduleTriggerDispatchReadinessService(_scheduleDeliveryProvenance),
            clock);
    }

    /// <summary>
    /// Loads one durable custom-loop run by run identifier.
    /// </summary>
    /// <param name="runId">The run identifier.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the run snapshot, or <see langword="null"/> when no run exists.</returns>
    public Task<LoopRunSnapshot?> GetCustomLoopRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _customLoops.GetAsync(runId, cancellationToken);
    }

    /// <summary>
    /// Lists the newest durable custom-loop runs across the workspace.
    /// </summary>
    /// <param name="maximumCount">The bounded maximum number of summaries to return.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains summaries in reverse-chronological order.</returns>
    public Task<IReadOnlyList<LoopRunSummarySnapshot>> ListCustomLoopRunsAsync(int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        return _customLoops.ListRecentAsync(maximumCount, cancellationToken);
    }

    /// <summary>
    /// Lists one cursor-bound page of durable custom-loop run summaries.
    /// </summary>
    /// <param name="maximumCount">The bounded page size.</param>
    /// <param name="loopId">An optional loop identifier filter.</param>
    /// <param name="cursor">An optional opaque continuation cursor bound to the filter and page shape.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the requested page and next cursor, when more evidence exists.</returns>
    public Task<LoopRunSummaryPageSnapshot> ListCustomLoopRunPageAsync(int maximumCount = 50, string? loopId = null, string? cursor = null, CancellationToken cancellationToken = default)
    {
        return _customLoops.ListPageAsync(maximumCount, loopId, cursor, cancellationToken);
    }

    /// <summary>
    /// Requests a durable pause for an admitted custom-loop run.
    /// </summary>
    /// <param name="input">The run, operation, and expected-state identity for the pause request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result reports the committed, replayed, rejected, or unknown control outcome.</returns>
    public Task<LoopRunControlResponse> PauseCustomLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.PauseAsync(input, cancellationToken);
    }

    /// <summary>
    /// Requests durable cancellation of an admitted custom-loop run.
    /// </summary>
    /// <param name="input">The run, operation, and expected-state identity for the cancellation request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result reports the committed, replayed, rejected, or unknown control outcome.</returns>
    public Task<LoopRunControlResponse> CancelCustomLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.CancelAsync(input, cancellationToken);
    }

    /// <summary>
    /// Explicitly resumes a paused custom-loop run when its durable evidence is eligible.
    /// </summary>
    /// <param name="input">The run, operation, and expected-state identity for the resume request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result reports the committed, replayed, rejected, or unknown control outcome.</returns>
    public Task<LoopRunControlResponse> ResumeCustomLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.ResumeAsync(input, cancellationToken);
    }

    /// <summary>Explicitly activates this process as the browser-independent host for canonical local governed-loop background work.</summary>
    /// <remarks>
    /// Normal Web, CLI, and request runtimes are inert until their process-level host calls this member. The retained
    /// coordinator borrows the factory-composed canonical run, queue, schedule, sleep, and evidence stores; callers cannot
    /// construct a second coordinator through this runtime. The returned activation outcome never changes whether ordinary
    /// custom-loop invocation is available when another live coordinator owns delivery.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel recovery and coordinator acquisition.</param>
    /// <returns>The typed coordinator ownership and readiness outcome projected through the shared Startup boundary.</returns>
    public async Task<AgentRuntimeGovernedLoopBackgroundStartResult> StartGovernedLoopLocalBackgroundWithStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var activationSequence = _governedBackgroundRuntimeHost.ActivationSequence;
        _governedBackgroundRuntimeHost.RequestActivation();
        var current = await _governedBackgroundRuntimeHost.ReadStatusAsync(cancellationToken);
        if (current.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.LivePeer)
        {
            return new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.OwnedByLivePeer,
                current.Readiness,
                current.Ownership,
                true,
                "governed_local_background_owned_by_live_peer: another process retains canonical background-work delivery.");
        }

        if (current.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Ready
            && current.Ownership == AgentRuntimeGovernedLoopBackgroundOwnership.Local)
        {
            return new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.AlreadyRunning,
                current.Readiness,
                current.Ownership,
                true,
                "governed_local_background_ready: this runtime already owns canonical background-work delivery.");
        }

        if (current.Readiness == AgentRuntimeGovernedLoopBackgroundReadiness.Draining)
        {
            return new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                current.Readiness,
                current.Ownership,
                true,
                "governed_local_background_draining: the prior stop request has not reached a durable safe boundary.");
        }

        var availability = await _customLoops.EnsureCustomExecutionAvailableAsync(cancellationToken);
        if (!availability.Available)
        {
            return new AgentRuntimeGovernedLoopBackgroundStartResult(
                AgentRuntimeGovernedLoopBackgroundStartStatus.Unavailable,
                AgentRuntimeGovernedLoopBackgroundReadiness.Unavailable,
                AgentRuntimeGovernedLoopBackgroundOwnership.Unknown,
                _customLoops.CustomExecutionReacquisitionAllowed,
                availability.Detail);
        }

        if (_governedBackgroundRuntimeHost.TryGetActivationResultAfter(activationSequence, out var activation))
        {
            return activation!;
        }

        return await _governedBackgroundRuntimeHost.StartAsync(cancellationToken);
    }

    /// <summary>Explicitly activates canonical local governed-loop background work through the legacy availability projection.</summary>
    /// <remarks>
    /// This compatibility member preserves the historical availability contract, including an available result when a live
    /// peer already owns delivery. Process hosts that need exact ownership, readiness, and retry semantics must call
    /// <see cref="StartGovernedLoopLocalBackgroundWithStatusAsync"/> instead.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel recovery and coordinator acquisition.</param>
    /// <returns>The historical availability projection for the canonical coordinator outcome.</returns>
    public async Task<CustomLoopExecutionActivationResult> StartGovernedLoopLocalBackgroundAsync(
        CancellationToken cancellationToken = default)
    {
        _governedBackgroundRuntimeHost.RequestActivation();
        var availability = await _customLoops.EnsureCustomExecutionAvailableAsync(cancellationToken);
        if (!availability.Available)
        {
            return new CustomLoopExecutionActivationResult(
                false,
                _customLoops.CustomExecutionReacquisitionAllowed,
                availability.Status,
                availability.Detail);
        }

        return await _governedBackgroundRuntimeHost.ActivateAsync(cancellationToken);
    }

    /// <summary>Explicitly activates canonical local background work for callers that previously requested only Wait recovery.</summary>
    /// <remarks>
    /// This compatibility-shaped name now activates the same canonical local coordinator that owns retained Wait recovery,
    /// schedule-finalization retry, and trigger dispatch. It never creates a second coordinator or workspace-store lifetime.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel recovery and coordinator acquisition.</param>
    /// <returns>The compatibility activation projection for the canonical coordinator outcome.</returns>
    public Task<CustomLoopExecutionActivationResult> StartGovernedWaitBackgroundAsync(
        CancellationToken cancellationToken = default)
        => StartGovernedLoopLocalBackgroundAsync(cancellationToken);

    /// <summary>Reads non-sensitive ownership and lifecycle readiness for canonical local governed-loop background work.</summary>
    /// <remarks>
    /// This member intentionally projects only process-host information. It does not expose durable coordinator records,
    /// owner identities, run contents, queue entries, or persistence implementation details.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel the bounded status read.</param>
    /// <returns>The current typed readiness and active-ownership classification.</returns>
    public Task<AgentRuntimeGovernedLoopBackgroundStatus> ReadGovernedLoopLocalBackgroundStatusAsync(
        CancellationToken cancellationToken = default)
        => _governedBackgroundRuntimeHost.ReadStatusAsync(cancellationToken);

    /// <summary>Requests an idempotent bounded drain of locally owned canonical governed-loop background work.</summary>
    /// <remarks>
    /// The call never fabricates a terminal or parked result when a one-shot is still executing. If its fixed safe drain
    /// bound expires, it returns <see cref="AgentRuntimeGovernedLoopBackgroundStopStatus.Draining"/> while the retained
    /// coordinator continues to stop admission and preserve durable evidence until a later status read reaches terminal state.
    /// Callers must retain this runtime until it is stopped or disposed.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel before the stop request begins.</param>
    /// <returns>The typed stop outcome or truthful bounded-drain state.</returns>
    public Task<AgentRuntimeGovernedLoopBackgroundStopResult> StopGovernedLoopLocalBackgroundAsync(
        CancellationToken cancellationToken = default)
        => _governedBackgroundRuntimeHost.StopAsync(cancellationToken);

    /// <summary>Waits for a previously requested local background stop to reach its exact safe boundary.</summary>
    /// <remarks>
    /// A bounded stop request may return <see cref="AgentRuntimeGovernedLoopBackgroundStopStatus.Draining"/> while one
    /// admitted one-shot still runs. This wait does not dispose the runtime; callers must retain this runtime and its
    /// stores until the returned result is terminal, then dispose the runtime through its normal lifetime owner.
    /// </remarks>
    /// <returns>The terminal stop result, or the already-stopped result when no stop is retained.</returns>
    public Task<AgentRuntimeGovernedLoopBackgroundStopResult> WaitForGovernedLoopLocalBackgroundStopAsync()
        => _governedBackgroundRuntimeHost.WaitForStopCompletionAsync();

    /// <summary>
    /// Attempts to handle a runtime command that does not require an initialized runtime instance.
    /// </summary>
    /// <param name="input">The candidate command text.</param>
    /// <param name="result">The projected command result, including the default unhandled projection when this method returns <see langword="false"/>.</param>
    /// <returns><see langword="true"/> when <paramref name="input"/> was a recognized static command; otherwise, <see langword="false"/>.</returns>
    public static bool TryHandleStaticRuntimeCommand(string input, out AgentRuntimeTurnResult result)
    {
        var handled = RuntimeCommandService.TryHandleStaticCommand(input, out var commandResult);
        result = AgentRuntimeTurnResultFactory.FromCommand(commandResult);
        return handled;
    }

    /// <summary>
    /// Disposes custom-loop hosting resources and then terminates the owned inference client.
    /// </summary>
    /// <remarks>
    /// When the canonical background coordinator is still draining an admitted one-shot, this method returns after the
    /// durable stop request is bounded and retains the complete runtime composition. A later disposal call waits for the
    /// deferred safe-boundary cleanup; inference, stores, and loop dependencies are never disposed while the coordinator
    /// can still call them.
    /// </remarks>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref _disposeCompletion, completion, null) is { } existingCompletion)
        {
            await existingCompletion.Task.ConfigureAwait(false);
            return;
        }

        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            Exception? backgroundDisposeFailure = null;
            try
            {
                await _governedBackgroundRuntimeHost.DisposeAsync();
            }
            catch (Exception exception)
            {
                // The background host retains its completion task even when initiating disposal fails. Continue through
                // that task so owned stores and inference are still cleaned up and the original failure is surfaced.
                backgroundDisposeFailure = exception;
            }

            var backgroundCompletion = _governedBackgroundRuntimeHost.WaitForDisposeCompletionAsync();
            if (!backgroundCompletion.IsCompleted)
            {
                _ = DisposeAfterBackgroundDrainAsync(backgroundCompletion, completion, DisposeOwnedResourcesAsync, backgroundDisposeFailure);
                return;
            }

            await DisposeAfterBackgroundDrainAsync(backgroundCompletion, completion, DisposeOwnedResourcesAsync, backgroundDisposeFailure);
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
            throw;
        }

        async Task DisposeOwnedResourcesAsync()
        {
            try
            {
                _governedLoops.Dispose();
            }
            finally
            {
                try
                {
                    await _customLoops.DisposeAsync();
                }
                finally
                {
                    try
                    {
                        if (_ownsCustomRunStore)
                        {
                            _customRunStore.Dispose();
                        }
                    }
                    finally
                    {
                        await _inferenceClient.DisposeAsync();
                    }
                }
            }
        }
    }

    private async Task DisposeAfterBackgroundDrainAsync(
        Task backgroundCompletion,
        TaskCompletionSource<bool> completion,
        Func<Task> disposeOwnedResources,
        Exception? initialFailure = null)
    {
        Exception? failure = initialFailure;
        try
        {
            await backgroundCompletion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null || ReferenceEquals(failure, exception)
                ? exception
                : new AggregateException(failure, exception);
        }

        try
        {
            await disposeOwnedResources().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = failure is null ? exception : new AggregateException(failure, exception);
        }

        if (failure is null)
        {
            completion.TrySetResult(true);
        }
        else
        {
            completion.TrySetException(failure);
        }
    }

    private async Task<AgentRuntimeTurnResult> RunModelTurnAsync(
        string message,
        Func<string, CancellationToken, Task>? responseChunkHandler,
        Func<string, CancellationToken, Task>? verboseContextHandler,
        CancellationToken cancellationToken,
        string? requestId)
    {
        Func<RuntimeDiagnosticMessage, CancellationToken, Task>? diagnosticHandler = null;
        if (_state.Verbose && verboseContextHandler is not null)
        {
            diagnosticHandler = (diagnostic, token) =>
            {
                return diagnostic.Kind == RuntimeDiagnosticKind.VerboseContext
                    ? verboseContextHandler(diagnostic.Content, token)
                    : Task.CompletedTask;
            };
        }

        var request = new DefaultConversationLoopTurnRequest(message, responseChunkHandler, diagnosticHandler, cancellationToken, requestId);
        var result = await _loopRunner.RunTurnAsync(request);
        _commandService.ClearPendingInput();
        if (result.UserMessageAccepted)
        {
            _state.MarkModelTurnStarted();
        }

        return AgentRuntimeTurnResultFactory.FromDefaultLoop(result);
    }
}
