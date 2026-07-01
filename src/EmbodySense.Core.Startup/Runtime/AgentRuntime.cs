using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Loops.Execution.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IAsyncDisposable _inferenceClient;
    private readonly IDefaultConversationLoopRunner _loopRunner;
    // TODO(runtime-turn-api-ergonomics): AgentRuntime still adapts command results, loop results, and transcript projections directly.
    // Revisit when the runtime can expose typed turn events that Web, CLI, and future loop-builder surfaces can consume without
    // each host reconstructing presentation-specific behavior.
    private readonly RuntimeSessionState _state = new();
    private readonly RuntimeCommandService _commandService;
    private readonly ConversationRuntimeState _conversationState;

    internal AgentRuntime(
        WorkspacePaths paths,
        AgentRuntimeSurface surface,
        IConversationMemoryStore conversationMemory,
        IReadOnlyList<LlmMessage> startupContext,
        ConversationRuntimeState conversationState,
        IAsyncDisposable inferenceClient,
        IDefaultConversationLoopRunner loopRunner)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(startupContext);
        ArgumentNullException.ThrowIfNull(conversationState);
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(loopRunner);

        Paths = paths;
        Surface = surface;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        _conversationState = conversationState;
        _inferenceClient = inferenceClient;
        _loopRunner = loopRunner;
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
            return ToRuntimeResult(commandResult);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        return await RunModelTurnAsync(input, responseChunkHandler, verboseContextHandler, cancellationToken);
    }

    public AgentRuntimeTurnResult SetVerbose(bool enabled)
    {
        _state.SetVerbose(enabled);
        return AgentRuntimeTurnResult.CommandOutput(enabled ? RuntimeCommandOutput.VerboseEnabledText : "Verbose mode disabled.");
    }

    public static bool TryHandleStaticRuntimeCommand(string input, out AgentRuntimeTurnResult result)
    {
        var handled = RuntimeCommandService.TryHandleStaticCommand(input, out var commandResult);
        result = ToRuntimeResult(commandResult);
        return handled;
    }

    public async ValueTask DisposeAsync()
    {
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

        var runIdentity = ToRuntimeRunIdentity(result.RunIdentity);

        if (result.Status == DefaultConversationLoopTurnStatus.Completed)
        {
            return AgentRuntimeTurnResult.MessageCompleted(result.AssistantOutput, runIdentity);
        }

        if (result.Status == DefaultConversationLoopTurnStatus.Cancelled)
        {
            return AgentRuntimeTurnResult.MessageCancelled(result.FailureDetail ?? "Turn was cancelled.", runIdentity);
        }

        if (result.Status == DefaultConversationLoopTurnStatus.Failed)
        {
            return AgentRuntimeTurnResult.MessageFailed(result.FailureDetail ?? "Default conversation loop turn failed.", runIdentity);
        }

        throw new InvalidOperationException($"Unsupported default conversation loop status: {result.Status}.");
    }

    private static AgentRuntimeTurnResult ToRuntimeResult(RuntimeCommandResult result)
    {
        if (result.ExitRequested)
        {
            return AgentRuntimeTurnResult.Exit();
        }

        var restoredMessages = result.RestoredMessages.Select(message => new AgentRuntimeTranscriptMessage(FormatRole(message.Role), message.Content)).ToArray();
        return AgentRuntimeTurnResult.CommandOutput(result.Output, result.Prompt, result.AwaitingInput, restoredMessages, result.ReplaceTranscript);
    }

    private static AgentRuntimeRunIdentity? ToRuntimeRunIdentity(LoopRunIdentity? runIdentity)
    {
        return runIdentity is null
            ? null
            : new AgentRuntimeRunIdentity(runIdentity.LoopId, runIdentity.RunId, runIdentity.RoleId);
    }

    private static string FormatRole(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "system",
            LlmMessageRole.User => "user",
            LlmMessageRole.Assistant => "assistant",
            LlmMessageRole.Tool => "tool",
            _ => role.ToString().ToLowerInvariant()
        };
    }
}
