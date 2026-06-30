using EmbodySense.Core.Application.Runtime;
using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class RuntimeCommandServiceTests
{
    [Fact]
    public async Task New_session_resets_memory_state_and_model_turn()
    {
        var memory = new FakeConversationMemoryStore();
        var resettableClient = new ResettableInferenceClient();
        var conversationState = new ConversationRuntimeState([LlmMessage.User("old prompt")], resettableClient);
        var runtimeState = new RuntimeSessionState();
        runtimeState.MarkModelTurnStarted();
        var service = new RuntimeCommandService(memory, [LlmMessage.System("startup")]);

        var result = await service.TryHandleAsync("/new", conversationState, runtimeState);

        Assert.True(result.Handled);
        Assert.Equal("Started a new conversation.", result.Output);
        Assert.False(runtimeState.ModelTurnStarted);
        Assert.Equal(1, memory.StartFreshConversationCallCount);
        Assert.Equal(1, resettableClient.ResetCount);
        var message = Assert.Single(conversationState.Messages);
        Assert.Equal(LlmMessageRole.System, message.Role);
        Assert.Equal("startup", message.Content);
    }

    [Fact]
    public async Task History_requires_loading_before_model_turn()
    {
        var memory = new FakeConversationMemoryStore();
        var conversationState = new ConversationRuntimeState();
        var runtimeState = new RuntimeSessionState();
        runtimeState.MarkModelTurnStarted();
        var service = new RuntimeCommandService(memory);

        var result = await service.TryHandleAsync("/history", conversationState, runtimeState);

        Assert.True(result.Handled);
        Assert.Equal(0, memory.ListConversationCallCount);
        Assert.Contains("before sending the first prompt", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task History_loads_selected_conversation_with_startup_messages()
    {
        var memory = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "saved prompt", false)
            ],
            LoadedMessages = [LlmMessage.User("saved prompt")]
        };
        var conversationState = new ConversationRuntimeState();
        var service = new RuntimeCommandService(memory, [LlmMessage.System("startup")]);

        var listed = await service.TryHandleAsync("/history", conversationState, new RuntimeSessionState());
        var loaded = await service.TryHandleAsync("1", conversationState, new RuntimeSessionState());

        Assert.True(listed.AwaitingInput);
        Assert.Contains("Stored conversations:", listed.Output, StringComparison.Ordinal);
        Assert.True(loaded.Handled);
        Assert.True(loaded.ReplaceTranscript);
        Assert.Equal("conv-1", memory.LoadedConversationId);
        Assert.Equal("conv-1", memory.ResumedConversationId);
        Assert.Collection(
            conversationState.Messages,
            message => Assert.Equal("startup", message.Content),
            message => Assert.Equal("saved prompt", message.Content));
    }

    [Fact]
    public async Task History_supports_deferred_cancel_and_invalid_selection()
    {
        var memory = new FakeConversationMemoryStore
        {
            Conversations =
            [
                new ConversationTranscriptListItem("conv-1", 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "saved prompt", false)
            ]
        };
        var service = new RuntimeCommandService(memory);
        var state = new ConversationRuntimeState();

        _ = await service.TryHandleAsync("/history", state, new RuntimeSessionState(), RuntimeCommandInteractionMode.DeferredSelection);
        var cancelled = await service.TryHandleAsync("/cancel", state, new RuntimeSessionState(), RuntimeCommandInteractionMode.DeferredSelection);
        _ = await service.TryHandleAsync("/history", state, new RuntimeSessionState(), RuntimeCommandInteractionMode.DeferredSelection);
        var invalid = await service.TryHandleAsync("99", state, new RuntimeSessionState(), RuntimeCommandInteractionMode.DeferredSelection);

        Assert.Equal("Conversation load cancelled.", cancelled.Output);
        Assert.Equal("Invalid conversation selection.", invalid.Output);
    }

    private sealed class FakeConversationMemoryStore : IConversationMemoryStore
    {
        public IReadOnlyList<ConversationTranscriptListItem> Conversations { get; init; } = [];

        public IReadOnlyList<LlmMessage> LoadedMessages { get; init; } = [];

        public int StartFreshConversationCallCount { get; private set; }

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
            StartFreshConversationCallCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default)
        {
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

    private sealed class ResettableInferenceClient : IResettableInferenceClient
    {
        public int ResetCount { get; private set; }

        public void ResetConversation()
        {
            ResetCount++;
        }
    }
}
