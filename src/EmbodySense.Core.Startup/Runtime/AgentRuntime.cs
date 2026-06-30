using EmbodySense.Core.Application.Loops.Execution;
using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IAsyncDisposable _inferenceClient;
    private readonly IDefaultConversationLoopRunner _loopRunner;
    private readonly RuntimeSurface _surface;
    private readonly RuntimeSessionState _state = new();
    private readonly RuntimeCommandService _commandService;
    private readonly ConversationRuntimeState _conversationState;

    internal AgentRuntime(
        WorkspacePaths paths,
        IConversationMemoryStore conversationMemory,
        IReadOnlyList<LlmMessage> startupContext,
        ConversationRuntimeState conversationState,
        IAsyncDisposable inferenceClient,
        IDefaultConversationLoopRunner loopRunner,
        RuntimeSurface surface)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(startupContext);
        ArgumentNullException.ThrowIfNull(conversationState);
        ArgumentNullException.ThrowIfNull(inferenceClient);
        ArgumentNullException.ThrowIfNull(loopRunner);
        ArgumentNullException.ThrowIfNull(surface);

        Paths = paths;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        _conversationState = conversationState;
        _inferenceClient = inferenceClient;
        _loopRunner = loopRunner;
        _surface = surface;
        _commandService = new RuntimeCommandService(conversationMemory, startupContext);
    }

    internal WorkspacePaths Paths { get; }

    internal IConversationMemoryStore ConversationMemory { get; }

    internal IReadOnlyList<LlmMessage> StartupContext { get; }

    internal IReadOnlyList<LlmMessage> Messages => _conversationState.Messages;

    public async Task<string> SendUserMessageAsync(
        string message,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<string, CancellationToken, Task>? verboseContextHandler = null,
        CancellationToken cancellationToken = default)
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

        var request = new DefaultConversationLoopTurnRequest(
            message,
            _surface,
            responseChunkHandler,
            diagnosticHandler,
            cancellationToken);
        var result = await _loopRunner.RunTurnAsync(request);
        _commandService.ClearPendingInput();

        if (result.Status == DefaultConversationLoopTurnStatus.Completed)
        {
            _state.MarkModelTurnStarted();
            return result.AssistantOutput;
        }

        if (result.Status == DefaultConversationLoopTurnStatus.Cancelled)
        {
            throw new OperationCanceledException(result.FailureDetail ?? "Turn was cancelled.", cancellationToken);
        }

        throw new InvalidOperationException(result.FailureDetail ?? "Default conversation loop turn failed.");
    }

    public AgentRuntimeCommandResult SetVerbose(bool enabled)
    {
        _state.SetVerbose(enabled);
        return AgentRuntimeCommandResult.HandledOutput(enabled ? RuntimeCommandOutput.VerboseEnabledText : "Verbose mode disabled.");
    }

    public static bool TryHandleStaticRuntimeCommand(string input, out AgentRuntimeCommandResult result)
    {
        var handled = RuntimeCommandService.TryHandleStaticCommand(input, out var commandResult);
        result = ToRuntimeResult(commandResult);
        return handled;
    }

    public async Task<AgentRuntimeCommandResult> TryHandleRuntimeCommandAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var result = await _commandService.TryHandleAsync(input, _conversationState, _state, RuntimeCommandInteractionMode.DeferredSelection, cancellationToken);
        return ToRuntimeResult(result);
    }

    public async Task<int> RunConsoleLoopAsync(
        IAgentRuntimeConsole console,
        string? banner = null,
        string prompt = RuntimeCommandOutput.UserPrompt,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);

        if (!string.IsNullOrEmpty(banner))
        {
            console.WriteLine(banner);
        }

        _state.SetVerbose(verbose);
        if (_state.Verbose)
        {
            console.WriteLine(RuntimeCommandOutput.VerboseEnabledText);
        }

        while (!_state.ExitRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            console.Write(prompt);
            var input = console.ReadLine();

            switch (input)
            {
                case null:
                    _state.RequestExit();
                    break;

                case var value when string.IsNullOrWhiteSpace(value):
                    break;

                default:
                    if (await TryHandleConsoleCommandAsync(input, console, cancellationToken))
                    {
                        break;
                    }

                    await RunConsoleModelTurnAsync(input, console, cancellationToken);
                    break;
            }
        }

        return 0;
    }

    public async ValueTask DisposeAsync()
    {
        await _inferenceClient.DisposeAsync();
    }

    private async Task<bool> TryHandleConsoleCommandAsync(
        string input,
        IAgentRuntimeConsole console,
        CancellationToken cancellationToken)
    {
        var result = await _commandService.TryHandleAsync(input, _conversationState, _state, cancellationToken: cancellationToken);
        if (!result.Handled)
        {
            return false;
        }

        await WriteConsoleCommandResultAsync(result, console, cancellationToken);
        return true;
    }

    private async Task WriteConsoleCommandResultAsync(
        RuntimeCommandResult result,
        IAgentRuntimeConsole console,
        CancellationToken cancellationToken)
    {
        WriteConsoleCommandResult(result, console);
        if (!result.AwaitingInput)
        {
            return;
        }

        var answer = console.ReadLine() ?? "";
        var answerResult = await _commandService.TryHandleAsync(answer, _conversationState, _state, cancellationToken: cancellationToken);
        WriteConsoleCommandResult(answerResult, console);
    }

    private static void WriteConsoleCommandResult(RuntimeCommandResult result, IAgentRuntimeConsole console)
    {
        if (result.ReplaceTranscript)
        {
            console.Clear();
            console.WriteLine(RuntimeCommandOutput.FormatRestoredConversation(result.RestoredMessages));
            console.WriteLine();
        }

        if (!string.IsNullOrEmpty(result.Output))
        {
            console.WriteLine(result.Output);
        }

        if (!string.IsNullOrEmpty(result.Prompt))
        {
            console.Write(result.Prompt);
        }
    }

    private async Task RunConsoleModelTurnAsync(
        string input,
        IAgentRuntimeConsole console,
        CancellationToken cancellationToken)
    {
        var wroteAssistantHeader = false;
        var wroteResponseChunk = false;
        var responseEndedWithNewLine = false;

        var response = await SendUserMessageAsync(
            input,
            (chunk, _) =>
            {
                if (!string.IsNullOrEmpty(chunk))
                {
                    if (!wroteAssistantHeader)
                    {
                        console.WriteLine(RuntimeCommandOutput.FormatMessageHeader(LlmMessageRole.Assistant));
                        wroteAssistantHeader = true;
                    }

                    console.Write(chunk);
                    wroteResponseChunk = true;
                    responseEndedWithNewLine = EndsWithNewLine(chunk);
                }

                return Task.CompletedTask;
            },
            (context, token) =>
            {
                console.WriteLine(context);
                console.WriteLine();
                return Task.CompletedTask;
            },
            cancellationToken);

        if (!wroteResponseChunk)
        {
            console.WriteLine(RuntimeCommandOutput.FormatMessageHeader(LlmMessageRole.Assistant));
            console.WriteLine(response);
        }
        else if (!responseEndedWithNewLine)
        {
            console.WriteLine();
        }
    }

    private static AgentRuntimeCommandResult ToRuntimeResult(RuntimeCommandResult result)
    {
        if (!result.Handled)
        {
            return AgentRuntimeCommandResult.NotHandled;
        }

        var output = string.IsNullOrWhiteSpace(result.Prompt)
            ? result.Output
            : string.IsNullOrWhiteSpace(result.Output) ? result.Prompt : result.Output + Environment.NewLine + result.Prompt;
        var restoredMessages = result.RestoredMessages.Select(message => new AgentRuntimeTranscriptMessage(FormatRole(message.Role), message.Content)).ToArray();
        return new AgentRuntimeCommandResult(true, output, result.ExitRequested, restoredMessages, result.ReplaceTranscript);
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

    private static bool EndsWithNewLine(string text)
    {
        return text.Length > 0 && text[^1] is '\n' or '\r';
    }
}
