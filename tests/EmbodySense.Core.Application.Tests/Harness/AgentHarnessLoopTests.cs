using System.Text;
using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;

namespace EmbodySense.Core.Application.Tests.Harness;

public sealed class AgentHarnessLoopTests
{
    [Fact]
    public async Task RunHarnessLoopAsync_ignores_blank_input_and_exits_on_end_of_input()
    {
        var harnessClient = new ScriptedHarnessClient("", "   ");
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, harnessClient, commandHandler);

        Assert.Equal(0, exitCode);
        Assert.Empty(inferenceClient.Requests);
        Assert.Equal(3, CountOccurrences(harnessClient.Output, "> "));
    }

    [Fact]
    public async Task RunHarnessLoopAsync_writes_configured_banner_and_prompt()
    {
        var harnessClient = new ScriptedHarnessClient("/exit");
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient);
        var options = new AgentHarnessLoopOptions { Banner = "custom banner", Prompt = "prompt> " };

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, harnessClient, commandHandler, options);

        Assert.Equal(0, exitCode);
        Assert.Contains("custom banner", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("prompt> ", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHarnessLoopAsync_handles_harness_commands_without_model_inference()
    {
        var harnessClient = new ScriptedHarnessClient("/help", "/exit");
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, harnessClient, commandHandler);

        Assert.Equal(0, exitCode);
        Assert.Empty(inferenceClient.Requests);
        Assert.Contains("Harness commands:", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("/new, /new-session", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHarnessLoopAsync_streams_unhandled_input_through_session()
    {
        var harnessClient = new ScriptedHarnessClient("hello", "/exit");
        var inferenceClient = new ScriptedInferenceClient("hello world");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, harnessClient, commandHandler);

        Assert.Equal(0, exitCode);
        var requestMessages = Assert.Single(inferenceClient.Requests);
        Assert.Collection(requestMessages, message => Assert.Equal("hello", message.Content));
        Assert.Contains("hello world", harnessClient.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(harnessClient.Output, "hello world"));
    }

    [Fact]
    public async Task RunHarnessLoopAsync_writes_response_text_when_client_does_not_stream()
    {
        var harnessClient = new ScriptedHarnessClient("hello", "/exit");
        var inferenceClient = new ScriptedInferenceClient("fallback response") { StreamResponses = false };
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, harnessClient, commandHandler);

        Assert.Equal(0, exitCode);
        Assert.Contains("fallback response", harnessClient.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(harnessClient.Output, "fallback response"));
    }

    [Fact]
    public async Task HarnessCommandHandler_new_session_resets_model_turn_state()
    {
        var harnessClient = new ScriptedHarnessClient();
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient, initialMessages: [LlmMessage.User("old prompt")]);
        var commandHandler = new HarnessCommandHandler(harnessClient, startupMessages: [LlmMessage.System("startup")]);
        var state = new HarnessLoopState();
        state.MarkModelTurnStarted();

        var handled = await commandHandler.TryHandleAsync("/new", session, state);

        Assert.True(handled);
        Assert.False(state.ModelTurnStarted);
        var message = Assert.Single(session.Messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Equal("startup", message.Content);
        Assert.Contains("Started a new conversation.", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_requires_loading_before_model_turn()
    {
        var harnessClient = new ScriptedHarnessClient();
        var store = new FakeConversationMemoryStore();
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient, store);
        var state = new HarnessLoopState();
        state.MarkModelTurnStarted();

        var handled = await commandHandler.TryHandleAsync("/history", session, state);

        Assert.True(handled);
        Assert.Equal(0, store.ListConversationCallCount);
        Assert.Contains("Load a stored conversation before sending the first prompt", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_reports_unavailable_store()
    {
        var harnessClient = new ScriptedHarnessClient();
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("Conversation history is not available for this session.", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_reports_empty_store()
    {
        var harnessClient = new ScriptedHarnessClient();
        var store = new FakeConversationMemoryStore();
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient, store);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Equal(1, store.ListConversationCallCount);
        Assert.Contains("No stored conversations were found.", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_can_be_cancelled_after_listing_conversations()
    {
        var harnessClient = new ScriptedHarnessClient("");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, true)
            ]
        };
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient, store);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("conv-1 (current)", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("(no user prompt)", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("Conversation load cancelled.", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_rejects_invalid_selection()
    {
        var harnessClient = new ScriptedHarnessClient("2");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "first prompt", false)
            ]
        };
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient, store);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("Invalid conversation selection.", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_loads_selected_conversation_with_startup_messages()
    {
        var harnessClient = new ScriptedHarnessClient("1");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new string('x', 120), false)
            ],
            LoadedMessages = [LlmMessage.User("restored prompt")]
        };
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient, store, [LlmMessage.System("startup")]);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Equal("conv-1", store.LoadedConversationId);
        Assert.Equal("conv-1", store.ResumedConversationId);
        Assert.Collection(
            session.Messages,
            message => Assert.Equal(LlmMessageRole.System, message.Role),
            message => Assert.Equal("restored prompt", message.Content));
        Assert.Equal(1, harnessClient.ClearCount);
        Assert.Contains("Loaded conversation transcript:", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("User:", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("restored prompt", harnessClient.Output, StringComparison.Ordinal);
        Assert.Contains("Loaded conversation `conv-1` (1 messages).", harnessClient.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_reports_load_failure()
    {
        var harnessClient = new ScriptedHarnessClient("1");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "first prompt", false)
            ],
            LoadException = new FormatException("bad transcript")
        };
        var inferenceClient = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(inferenceClient);
        var commandHandler = new HarnessCommandHandler(harnessClient, store);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("Could not load conversation: bad transcript", harnessClient.Output, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        return text.Split(value, StringSplitOptions.None).Length - 1;
    }

    private sealed class ScriptedHarnessClient(params string[] inputs) : IHarnessClient
    {
        private readonly Queue<string?> _inputs = new(inputs);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public int ClearCount { get; private set; }

        public string? ReadLine()
        {
            return _inputs.Count == 0 ? null : _inputs.Dequeue();
        }

        public void Clear()
        {
            ClearCount++;
        }

        public void Write(string value)
        {
            _output.Append(value);
        }

        public void WriteLine(string value = "")
        {
            _output.AppendLine(value);
        }
    }

    private sealed class ScriptedInferenceClient(params string[] outputs) : ILlmInferenceClient
    {
        private readonly Queue<string> _outputs = new(outputs);

        public List<IReadOnlyList<LlmMessage>> Requests { get; } = [];

        public bool StreamResponses { get; init; } = true;

        public async Task<LlmInferenceResponse> GenerateAsync(
            LlmInferenceRequest request,
            Func<string, CancellationToken, Task>? responseChunkHandler = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Messages.ToArray());
            var output = _outputs.Dequeue();

            if (StreamResponses && responseChunkHandler is not null)
            {
                await responseChunkHandler(output, cancellationToken);
            }

            return new LlmInferenceResponse(output, LlmInferenceSurface.OpenAiCodex);
        }
    }

    private sealed class FakeConversationMemoryStore : IConversationMemoryStore
    {
        public IReadOnlyList<ConversationTranscriptListItem> Conversations { get; init; } = [];

        public IReadOnlyList<LlmMessage> LoadedMessages { get; init; } = [];

        public Exception? LoadException { get; init; }

        public int ListConversationCallCount { get; private set; }

        public string? LoadedConversationId { get; private set; }

        public string? ResumedConversationId { get; private set; }

        public Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LlmMessage>>([]);
        }

        public Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default)
        {
            ListConversationCallCount++;
            return Task.FromResult(Conversations);
        }

        public Task StartFreshConversationAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            if (LoadException is not null)
            {
                throw LoadException;
            }

            LoadedConversationId = conversationId;
            return Task.FromResult(LoadedMessages);
        }

        public Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ResumedConversationId = conversationId;
            return Task.CompletedTask;
        }

        public Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(string query, int limit = 20, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ConversationMemorySearchResult>>([]);
        }
    }
}
