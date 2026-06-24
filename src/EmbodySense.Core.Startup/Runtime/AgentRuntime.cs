using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Startup.Runtime;

public sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IAsyncDisposable _inferenceClient;

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
    }

    internal WorkspacePaths Paths { get; }

    internal IConversationMemoryStore ConversationMemory { get; }

    internal IReadOnlyList<LlmMessage> StartupContext { get; }

    internal AgentHarnessSession Session { get; }

    public async Task<string> SendUserMessageAsync(
        string message,
        Func<string, CancellationToken, Task>? responseChunkHandler = null,
        CancellationToken cancellationToken = default)
    {
        var response = await Session.SendUserMessageAsync(message, responseChunkHandler, cancellationToken);
        return response.OutputText;
    }

    public Task<int> RunConsoleLoopAsync(
        IAgentRuntimeConsole console,
        string? banner = null,
        string prompt = "> ",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(console);

        var client = new HarnessClientAdapter(console);
        var commandHandler = new HarnessCommandHandler(client, ConversationMemory, StartupContext);
        var options = new AgentHarnessLoopOptions { Banner = banner, Prompt = prompt };
        return AgentHarnessLoop.RunHarnessLoopAsync(Session, client, commandHandler, options, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _inferenceClient.DisposeAsync();
    }
}
