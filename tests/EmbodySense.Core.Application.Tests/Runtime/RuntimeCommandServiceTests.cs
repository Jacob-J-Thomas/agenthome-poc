using EmbodySense.Core.Application.Inference;
using EmbodySense.Core.Application.Runtime.Commands;
using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Application.Runtime.Models;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class RuntimeCommandServiceTests
{
    [Fact]
    public void Help_text_is_generated_from_command_registry()
    {
        Assert.True(RuntimeCommandRegistry.TryMatch("/COMMANDS", out var help));
        Assert.Equal(RuntimeCommandId.Help, help.Id);
        Assert.True(RuntimeCommandRegistry.TryMatch("/verbose true", out var verboseEnable));
        Assert.Equal(RuntimeCommandId.VerboseEnable, verboseEnable.Id);
        Assert.Contains("/history, /conversations, /load - load a saved conversation", RuntimeCommandOutput.HelpText, StringComparison.Ordinal);
        Assert.DoesNotContain("/cancel", RuntimeCommandOutput.HelpText, StringComparison.Ordinal);
    }

    [Fact]
    public void Static_command_handling_rejects_blank_input_and_returns_help()
    {
        Assert.False(RuntimeCommandService.TryHandleStaticCommand(" ", out var blank));
        Assert.Same(RuntimeCommandResult.NotHandled, blank);

        Assert.True(RuntimeCommandService.TryHandleStaticCommand("/help", out var help));
        Assert.Equal(RuntimeCommandOutput.HelpText, help.Output);
    }

    [Fact]
    public async Task Session_commands_report_and_change_runtime_state_through_the_public_boundary()
    {
        var service = new RuntimeCommandService();
        var conversationState = new ConversationRuntimeState();
        var runtimeState = new RuntimeSessionState();

        var unknown = await service.TryHandleAsync("/not-a-command", conversationState, runtimeState);
        var initialStatus = await service.TryHandleAsync("/verbose", conversationState, runtimeState);
        var enabled = await service.TryHandleAsync("/verbose true", conversationState, runtimeState);
        var enabledStatus = await service.TryHandleAsync("/verbose", conversationState, runtimeState);
        var disabled = await service.TryHandleAsync("/verbose false", conversationState, runtimeState);
        var exit = await service.TryHandleAsync("/exit", conversationState, runtimeState);

        Assert.False(unknown.Handled);
        Assert.Equal("Verbose mode is off.", initialStatus.Output);
        Assert.Equal(RuntimeCommandOutput.VerboseEnabledText, enabled.Output);
        Assert.Equal("Verbose mode is on.", enabledStatus.Output);
        Assert.Equal("Verbose mode disabled.", disabled.Output);
        Assert.False(runtimeState.Verbose);
        Assert.True(exit.ExitRequested);
        Assert.True(runtimeState.ExitRequested);
    }

    [Fact]
    public async Task History_explains_when_storage_is_unavailable_or_empty()
    {
        var conversationState = new ConversationRuntimeState();
        var runtimeState = new RuntimeSessionState();

        var unavailable = await new RuntimeCommandService().TryHandleAsync("/history", conversationState, runtimeState);
        var empty = await new RuntimeCommandService(new FakeConversationMemoryStore()).TryHandleAsync("/history", conversationState, runtimeState);

        Assert.Equal("Conversation history is not available for this session.", unavailable.Output);
        Assert.Equal("No stored conversations were found.", empty.Output);
    }

    [Fact]
    public async Task Cancel_is_pending_input_command_only()
    {
        var service = new RuntimeCommandService();
        var state = new ConversationRuntimeState();

        var result = await service.TryHandleAsync("/cancel", state, new RuntimeSessionState());

        Assert.False(result.Handled);
    }

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
        Assert.Collection(
            conversationState.ContextMessages,
            message => Assert.Equal(RuntimeContextSource.StartupContext, message.Source),
            message =>
            {
                Assert.Equal(RuntimeContextSource.RestoredConversationHistory, message.Source);
                Assert.Contains("conv-1", message.Detail, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task History_supports_cancel_invalid_selection_and_command_interruption()
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

        _ = await service.TryHandleAsync("/history", state, new RuntimeSessionState());
        var cancelled = await service.TryHandleAsync("/cancel", state, new RuntimeSessionState());
        _ = await service.TryHandleAsync("/history", state, new RuntimeSessionState());
        var invalid = await service.TryHandleAsync("99", state, new RuntimeSessionState());
        _ = await service.TryHandleAsync("/history", state, new RuntimeSessionState());
        var invalidSlash = await service.TryHandleAsync("/not-a-command", state, new RuntimeSessionState());
        _ = await service.TryHandleAsync("/history", state, new RuntimeSessionState());
        var interrupted = await service.TryHandleAsync("/new", state, new RuntimeSessionState());

        Assert.Equal("Conversation load cancelled.", cancelled.Output);
        Assert.Equal("Invalid conversation selection.", invalid.Output);
        Assert.Equal("Invalid conversation selection.", invalidSlash.Output);
        Assert.Equal("Started a new conversation.", interrupted.Output);
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

        public Task<ConversationMemorySnapshot> LoadCurrentConversationSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ConversationMemorySnapshot("current", "runtime-command-version", []));
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

        public Task<bool> TryAppendMessageAsync(string expectedConversationId, string expectedConversationVersion, IReadOnlyList<LlmMessage> expectedPrefix, LlmMessage message, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
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
