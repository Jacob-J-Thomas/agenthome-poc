using EmbodySense.Core.Harness;
using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory;
using EmbodySense.Core.Workspace.Models;
using EmbodySense.Tests.Support;

namespace EmbodySense.Tests.Core.Harness;

public sealed class AgentHarnessSessionTests
{
    [Fact]
    public async Task SendUserMessageAsync_stores_user_and_completed_assistant_response()
    {
        var client = new ScriptedInferenceClient("completed response");
        var session = new AgentHarnessSession(client);

        var response = await session.SendUserMessageAsync("hello");

        Assert.Equal("completed response", response.OutputText);
        Assert.Single(client.Requests);
        Assert.Collection(
            session.Messages,
            message =>
            {
                Assert.Equal(LlmMessageRole.User, message.Role);
                Assert.Equal("hello", message.Content);
            },
            message =>
            {
                Assert.Equal(LlmMessageRole.Assistant, message.Role);
                Assert.Equal("completed response", message.Content);
            });
    }

    [Fact]
    public async Task SendUserMessageAsync_streams_chunks_through_single_inference_path()
    {
        var client = new ScriptedInferenceClient("streamed response");
        var session = new AgentHarnessSession(client);
        var chunks = new List<string>();

        var response = await session.SendUserMessageAsync("hello", (chunk, _) =>
        {
            chunks.Add(chunk);
            return Task.CompletedTask;
        });

        Assert.Equal("streamed response", response.OutputText);
        Assert.Equal("streamed response", string.Concat(chunks));
        Assert.Single(client.Requests);
        Assert.Contains(session.Messages, message => message.Role == LlmMessageRole.Assistant && message.Content == "streamed response");
    }

    [Fact]
    public async Task SendUserMessageAsync_persists_user_and_assistant_messages_to_conversation_memory()
    {
        using var workspace = new TestWorkspace();
        var memoryStore = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        var client = new ScriptedInferenceClient("remembered response");
        var session = new AgentHarnessSession(client, memoryStore);

        await session.SendUserMessageAsync("remember this");

        var loadedMessages = await memoryStore.LoadCurrentConversationAsync();
        Assert.Collection(
            loadedMessages,
            message =>
            {
                Assert.Equal(LlmMessageRole.User, message.Role);
                Assert.Equal("remember this", message.Content);
            },
            message =>
            {
                Assert.Equal(LlmMessageRole.Assistant, message.Role);
                Assert.Equal("remembered response", message.Content);
            });
    }

    [Fact]
    public async Task SendUserMessageAsync_includes_restored_messages_in_next_inference_request()
    {
        var client = new ScriptedInferenceClient("new response");
        var restoredMessages = new[]
        {
            LlmMessage.User("old question"),
            LlmMessage.Assistant("old answer")
        };
        var session = new AgentHarnessSession(client, initialMessages: restoredMessages);

        await session.SendUserMessageAsync("new question");

        var requestMessages = Assert.Single(client.Requests);
        Assert.Collection(
            requestMessages,
            message => Assert.Equal("old question", message.Content),
            message => Assert.Equal("old answer", message.Content),
            message => Assert.Equal("new question", message.Content));
    }

    [Fact]
    public void ReplaceMessages_replaces_in_memory_conversation_state()
    {
        var client = new ResettableInferenceClient();
        var session = new AgentHarnessSession(client, initialMessages: [LlmMessage.User("old prompt")]);

        session.ReplaceMessages([LlmMessage.User("loaded prompt")]);

        Assert.Equal(1, client.ResetCount);
        var message = Assert.Single(session.Messages);
        Assert.Equal("loaded prompt", message.Content);
    }

    private sealed class ScriptedInferenceClient(params string[] outputs) : ILlmInferenceClient
    {
        private readonly Queue<string> _outputs = new(outputs);

        public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToArray());
            var output = _outputs.Dequeue();

            if (responseChunkHandler is not null)
            {
                var midpoint = output.Length / 2;
                if (midpoint > 0)
                {
                    await responseChunkHandler(output[..midpoint], cancellationToken);
                    await responseChunkHandler(output[midpoint..], cancellationToken);
                }
                else
                {
                    await responseChunkHandler(output, cancellationToken);
                }
            }

            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex);
        }
    }

    private sealed class ResettableInferenceClient : ILlmInferenceClient, IResettableInferenceClient
    {
        public int ResetCount { get; private set; }

        public void ResetConversation()
        {
            ResetCount++;
        }

        public Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
