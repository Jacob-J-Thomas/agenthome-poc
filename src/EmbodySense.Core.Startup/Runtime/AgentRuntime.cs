using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Startup.Loops.Execution.Models;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Loops.Execution;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>
/// Exposes one composed EmbodySense conversation runtime through the interface-safe Core.Startup boundary.
/// </summary>
/// <remarks>
/// The runtime owns its inference client and custom-loop facade. Callers must dispose the instance to release the app-server
/// process, cancellation host, and workspace execution-gate resources. Turn cancellation is reported as a turn result when
/// accepted by the default loop; validation, persistence, and transport failures otherwise propagate.
/// </remarks>
public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IAsyncDisposable _inferenceClient;
    private readonly IDefaultConversationLoopRunner _loopRunner;
    private readonly RuntimeSessionState _state = new();
    private readonly RuntimeCommandService _commandService;
    private readonly ConversationRuntimeState _conversationState;
    private readonly CustomLoopRuntimeFacade _customLoops;

    internal AgentRuntime(
        WorkspacePaths paths,
        AgentRuntimeSurface surface,
        IConversationMemoryStore conversationMemory,
        IReadOnlyList<LlmMessage> startupContext,
        ConversationRuntimeState conversationState,
        IAsyncDisposable inferenceClient,
        IDefaultConversationLoopRunner loopRunner,
        CustomLoopRuntimeFacade customLoops,
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
        ArgumentNullException.ThrowIfNull(codexRuntimeStatus);

        Paths = paths;
        Surface = surface;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        _conversationState = conversationState;
        _inferenceClient = inferenceClient;
        _loopRunner = loopRunner;
        _customLoops = customLoops;
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
    /// <param name="input">The command or user message to process.</param>
    /// <param name="responseChunkHandler">An optional callback for streamed assistant-message deltas.</param>
    /// <param name="verboseContextHandler">An optional callback for verbose context diagnostics when verbose mode is enabled.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result projects command output, transcript events, completion, cancellation, or failure.</returns>
    public async Task<AgentRuntimeTurnResult> RunTurnAsync(
        string input,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<string, CancellationToken, Task>? verboseContextHandler = null,
        CancellationToken cancellationToken = default)
    {
        var commandResult = await _commandService.TryHandleAsync(input, _conversationState, _state, cancellationToken);
        if (commandResult.Handled)
        {
            return AgentRuntimeTurnResultFactory.FromCommand(commandResult);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return await RunModelTurnAsync(input, responseChunkHandler, verboseContextHandler, cancellationToken);
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
        CancellationToken cancellationToken)
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

        var request = new DefaultConversationLoopTurnRequest(message, responseChunkHandler, diagnosticHandler, cancellationToken);
        var result = await _loopRunner.RunTurnAsync(request);
        _commandService.ClearPendingInput();
        if (result.UserMessageAccepted)
        {
            _state.MarkModelTurnStarted();
        }

        return AgentRuntimeTurnResultFactory.FromDefaultLoop(result);
    }
}
