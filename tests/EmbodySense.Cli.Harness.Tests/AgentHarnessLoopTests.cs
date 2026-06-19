using System.Text;
using EmbodySense.Cli.Harness;
using EmbodySense.Core.Application.Harness;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;

namespace EmbodySense.Cli.Harness.Tests;

public sealed class AgentHarnessLoopTests
{
    [Fact]
    public async Task RunHarnessLoopAsync_ignores_blank_input_and_exits_on_end_of_input()
    {
        var terminal = new ScriptedHarnessTerminal("", "   ");
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler, terminal);

        Assert.Equal(0, exitCode);
        Assert.Empty(client.Requests);
        Assert.Equal(3, CountOccurrences(terminal.Output, "> "));
    }

    [Fact]
    public async Task RunHarnessLoopAsync_handles_harness_commands_without_model_inference()
    {
        var terminal = new ScriptedHarnessTerminal("/help", "/exit");
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler, terminal);

        Assert.Equal(0, exitCode);
        Assert.Empty(client.Requests);
        Assert.Contains("Harness commands:", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("/new, /new-session", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHarnessLoopAsync_streams_unhandled_input_through_session()
    {
        var terminal = new ScriptedHarnessTerminal("hello", "/exit");
        var client = new ScriptedInferenceClient("hello world");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler, terminal);

        Assert.Equal(0, exitCode);
        var requestMessages = Assert.Single(client.Requests);
        Assert.Collection(requestMessages, message => Assert.Equal("hello", message.Content));
        Assert.Contains("hello world", terminal.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(terminal.Output, "hello world"));
    }

    [Fact]
    public async Task RunHarnessLoopAsync_writes_response_text_when_client_does_not_stream()
    {
        var terminal = new ScriptedHarnessTerminal("hello", "/exit");
        var client = new ScriptedInferenceClient("fallback response") { StreamResponses = false };
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var exitCode = await AgentHarnessLoop.RunHarnessLoopAsync(session, commandHandler, terminal);

        Assert.Equal(0, exitCode);
        Assert.Contains("fallback response", terminal.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(terminal.Output, "fallback response"));
    }

    [Fact]
    public async Task HarnessCommandHandler_new_session_resets_model_turn_state()
    {
        var terminal = new ScriptedHarnessTerminal();
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client, initialMessages: [LlmMessage.User("old prompt")]);
        var commandHandler = new HarnessCommandHandler(startupMessages: [LlmMessage.System("startup")], terminal: terminal);
        var state = new HarnessLoopState();
        state.MarkModelTurnStarted();

        var handled = await commandHandler.TryHandleAsync("/new", session, state);

        Assert.True(handled);
        Assert.False(state.ModelTurnStarted);
        var message = Assert.Single(session.Messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Equal("startup", message.Content);
        Assert.Contains("Started a new conversation.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_requires_loading_before_model_turn()
    {
        var terminal = new ScriptedHarnessTerminal();
        var store = new FakeConversationMemoryStore();
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(store, terminal: terminal);
        var state = new HarnessLoopState();
        state.MarkModelTurnStarted();

        var handled = await commandHandler.TryHandleAsync("/history", session, state);

        Assert.True(handled);
        Assert.Equal(0, store.ListConversationCallCount);
        Assert.Contains("Load a stored conversation before sending the first prompt", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_reports_unavailable_store()
    {
        var terminal = new ScriptedHarnessTerminal();
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(terminal: terminal);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("Conversation history is not available for this session.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_reports_empty_store()
    {
        var terminal = new ScriptedHarnessTerminal();
        var store = new FakeConversationMemoryStore();
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(store, terminal: terminal);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Equal(1, store.ListConversationCallCount);
        Assert.Contains("No stored conversations were found.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_can_be_cancelled_after_listing_conversations()
    {
        var terminal = new ScriptedHarnessTerminal("");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, true)
            ]
        };
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(store, terminal: terminal);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("conv-1 (current)", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("(no user prompt)", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("Conversation load cancelled.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_rejects_invalid_selection()
    {
        var terminal = new ScriptedHarnessTerminal("2");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "first prompt", false)
            ]
        };
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(store, terminal: terminal);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("Invalid conversation selection.", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_loads_selected_conversation_with_startup_messages()
    {
        var terminal = new ScriptedHarnessTerminal("1");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new string('x', 120), false)
            ],
            LoadedMessages = [LlmMessage.User("restored prompt")]
        };
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(store, startupMessages: [LlmMessage.System("startup")], terminal: terminal);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Equal("conv-1", store.LoadedConversationId);
        Assert.Equal("conv-1", store.ResumedConversationId);
        Assert.Collection(
            session.Messages,
            message => Assert.Equal(LlmMessageRole.System, message.Role),
            message => Assert.Equal("restored prompt", message.Content));
        Assert.Contains("Loaded conversation `conv-1` (1 messages).", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessCommandHandler_history_reports_load_failure()
    {
        var terminal = new ScriptedHarnessTerminal("1");
        var store = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 3, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "first prompt", false)
            ],
            LoadException = new FormatException("bad transcript")
        };
        var client = new ScriptedInferenceClient("unused");
        var session = new AgentHarnessSession(client);
        var commandHandler = new HarnessCommandHandler(store, terminal: terminal);

        var handled = await commandHandler.TryHandleAsync("/history", session, new HarnessLoopState());

        Assert.True(handled);
        Assert.Contains("Could not load conversation: bad transcript", terminal.Output, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        return text.Split(value, StringSplitOptions.None).Length - 1;
    }

    private sealed class ScriptedHarnessTerminal(params string[] inputs) : IHarnessTerminal
    {
        private readonly Queue<string?> _inputs = new(inputs);
        private readonly StringBuilder _output = new();

        public string Output => _output.ToString();

        public string? ReadLine()
        {
            return _inputs.Count == 0 ? null : _inputs.Dequeue();
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
