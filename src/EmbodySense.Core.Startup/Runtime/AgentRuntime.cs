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
        CustomLoopRuntimeFacade customLoops)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(startupContext);
        ArgumentNullException.ThrowIfNull(conversationState);
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(customLoops);

        Paths = paths;
        Surface = surface;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        _conversationState = conversationState;
        _inferenceClient = inferenceClient;
        _loopRunner = loopRunner;
        _customLoops = customLoops;
        _commandService = new RuntimeCommandService(conversationMemory, startupContext);
    }

    internal WorkspacePaths Paths { get; }

    public AgentRuntimeSurface Surface { get; }

    internal IConversationMemoryStore ConversationMemory { get; }

    internal IReadOnlyList<LlmMessage> StartupContext { get; }

    internal IReadOnlyList<LlmMessage> Messages => _conversationState.Messages;

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

    public AgentRuntimeTurnResult SetVerbose(bool enabled)
    {
        _state.SetVerbose(enabled);
        return AgentRuntimeTurnResult.CommandOutput(enabled ? RuntimeCommandOutput.VerboseEnabledText : "Verbose mode disabled.");
    }

    public Task<LoopRunInvocationResponse> InvokeCustomLoopAsync(LoopRunInvocationInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.InvokeAsync(input, cancellationToken);
    }

    public Task<LoopRunSnapshot?> GetCustomLoopRunAsync(string runId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        return _customLoops.GetAsync(runId, cancellationToken);
    }

    public Task<IReadOnlyList<LoopRunSummarySnapshot>> ListCustomLoopRunsAsync(int maximumCount = 50, CancellationToken cancellationToken = default)
    {
        return _customLoops.ListRecentAsync(maximumCount, cancellationToken);
    }

    public Task<LoopRunControlResponse> PauseCustomLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.PauseAsync(input, cancellationToken);
    }

    public Task<LoopRunControlResponse> CancelCustomLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.CancelAsync(input, cancellationToken);
    }

    public Task<LoopRunControlResponse> ResumeCustomLoopAsync(LoopRunControlInput input, CancellationToken cancellationToken = default)
    {
        return _customLoops.ResumeAsync(input, cancellationToken);
    }

    public static bool TryHandleStaticRuntimeCommand(string input, out AgentRuntimeTurnResult result)
    {
        var handled = RuntimeCommandService.TryHandleStaticCommand(input, out var commandResult);
        result = AgentRuntimeTurnResultFactory.FromCommand(commandResult);
        return handled;
    }

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
