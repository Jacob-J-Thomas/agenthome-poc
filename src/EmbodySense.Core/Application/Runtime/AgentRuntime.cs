using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Persistence.Workspace.Models;

namespace EmbodySense.Core.Application.Runtime;

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

    public WorkspacePaths Paths { get; }

    public IConversationMemoryStore ConversationMemory { get; }

    public IReadOnlyList<LlmMessage> StartupContext { get; }

    public AgentHarnessSession Session { get; }

    public async ValueTask DisposeAsync()
    {
        await _inferenceClient.DisposeAsync();
    }
}
