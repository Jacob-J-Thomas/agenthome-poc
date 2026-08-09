using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Application.Loops.Protocol;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime.Models;
using EmbodySense.Core.Application.Triggers;
using EmbodySense.Core.Persistence.Triggers;
using EmbodySense.Core.Startup.Triggers;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Exposes one composed EmbodySense conversation runtime through the interface-safe Core.Startup boundary.
/// </summary>
/// <remarks>
/// The runtime owns its inference client and custom-loop facade. Callers must dispose the instance to release the app-server
/// process, cancellation host, and workspace execution-gate resources. Once the default loop accepts a model turn, cancellation,
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
    private readonly CustomLoopRuntimeFacade _customLoops;
    private readonly DefaultConversationTurnReviewService _defaultConversationReviews;

    internal AgentRuntime(
        WorkspacePaths paths,
        AgentRuntimeSurface surface,
        IConversationMemoryStore conversationMemory,
        IReadOnlyList<LlmMessage> startupContext,
        ConversationRuntimeState conversationState,
        IAsyncDisposable inferenceClient,
        IDefaultConversationLoopRunner loopRunner,
        CustomLoopRuntimeFacade customLoops,
        DefaultConversationTurnReviewService defaultConversationReviews,
        CodexRuntimeStatus codexRuntimeStatus)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(startupContext);
        ArgumentNullException.ThrowIfNull(conversationState);
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(customLoops);
        ArgumentNullException.ThrowIfNull(defaultConversationReviews);
        ArgumentNullException.ThrowIfNull(codexRuntimeStatus);

        Paths = paths;
        Surface = surface;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        _conversationState = conversationState;
        _inferenceClient = inferenceClient;
        _loopRunner = loopRunner;
        _customLoops = customLoops;
        _defaultConversationReviews = defaultConversationReviews;
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
    /// <param name="input">The immutable definition and operation identity used for admission.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result contains the durable admission receipt and projected run evidence.</returns>
    public Task<LoopRunInvocationResponse> InvokeCustomLoopAsync(LoopRunInvocationInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.InvokeAsync(input, cancellationToken);
    }

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
        var store = new TriggerQueueStore(Paths, timeProvider: clock);
        var service = new TriggerWorkerService(store, new TriggerWorkerCurrentEvidenceAuthorizerAdapter(authorizer), new TriggerCustomLoopDispatcher(_customLoops), clock);
        return new TriggerWorkerRuntimeFacade(store, service);
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
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async ValueTask DisposeAsync()
    {
        await _customLoops.DisposeAsync();
        await _inferenceClient.DisposeAsync();
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
