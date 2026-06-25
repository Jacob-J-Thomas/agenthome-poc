using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IAsyncDisposable _inferenceClient;
    private readonly HarnessLoopState _state = new();
    private readonly HarnessCommandService _commandService;

    internal AgentRuntime(
        WorkspacePaths paths,
        IConversationMemoryStore conversationMemory,
        IReadOnlyList<LlmMessage> startupContext,
        AgentHarnessSession session,
        IAsyncDisposable inferenceClient)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(conversationMemory);
        ArgumentNullException.ThrowIfNull(startupContext);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inferenceClient);

        Paths = paths;
        ConversationMemory = conversationMemory;
        StartupContext = startupContext;
        Session = session;
        _inferenceClient = inferenceClient;
        _commandService = new HarnessCommandService(conversationMemory, startupContext);
    }

    internal WorkspacePaths Paths { get; }

    internal IConversationMemoryStore ConversationMemory { get; }

    internal IReadOnlyList<LlmMessage> StartupContext { get; }

    internal AgentHarnessSession Session { get; }

    public async Task<string> SendUserMessageAsync(
        string message,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        Func<string, CancellationToken, Task>? verboseContextHandler = null,
        CancellationToken cancellationToken = default)
    {
        if (_state.Verbose && verboseContextHandler is not null)
        {
            var visibleContext = Session.Messages.Concat([LlmMessage.User(message)]).ToArray();
            await verboseContextHandler(HarnessCommandOutput.FormatVerboseContext(visibleContext), cancellationToken);
        }

        var response = await Session.SendUserMessageAsync(message, responseChunkHandler, cancellationToken);
        _commandService.ClearPendingInput();
        _state.MarkModelTurnStarted();
        return response.OutputText;
    }

    public AgentRuntimeCommandResult SetVerbose(bool enabled)
    {
        _state.SetVerbose(enabled);
        return AgentRuntimeCommandResult.HandledOutput(enabled ? HarnessCommandOutput.VerboseEnabledText : "Verbose mode disabled.");
    }

    public static bool TryHandleStaticHarnessCommand(string input, out AgentRuntimeCommandResult result)
    {
        var handled = HarnessCommandService.TryHandleStaticCommand(input, out var commandResult);
        result = ToRuntimeResult(commandResult);
        return handled;
    }

    public async Task<AgentRuntimeCommandResult> TryHandleHarnessCommandAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);

        var result = await _commandService.TryHandleAsync(input, Session, _state, HarnessCommandInteractionMode.DeferredSelection, cancellationToken);
        return ToRuntimeResult(result);
    }

    public Task<int> RunConsoleLoopAsync(
        IAgentRuntimeConsole console,
        string? banner = null,
        string prompt = HarnessCommandOutput.UserPrompt,
        bool verbose = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);

        var client = new HarnessClientAdapter(console);
        var commandHandler = new HarnessCommandHandler(client, ConversationMemory, StartupContext);
        var options = new AgentHarnessLoopOptions { Banner = banner, Prompt = prompt, Verbose = verbose };
        return AgentHarnessLoop.RunHarnessLoopAsync(Session, client, commandHandler, options, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _inferenceClient.DisposeAsync();
    }

    private static AgentRuntimeCommandResult ToRuntimeResult(HarnessCommandResult result)
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
}
