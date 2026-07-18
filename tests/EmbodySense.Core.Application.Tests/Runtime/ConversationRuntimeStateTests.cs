using EmbodySense.Core.Application.Runtime.State;
using EmbodySense.Core.Application.Runtime.Models;
using EmbodySense.Core.Common.Inference.Models;

namespace EmbodySense.Core.Application.Tests.Runtime;

public sealed class ConversationRuntimeStateTests
{
    [Fact]
    public void Context_message_reads_are_immutable_snapshots_while_concurrent_appends_remain_safe()
    {
        var state = new ConversationRuntimeState([LlmMessage.System("startup")]);
        var snapshot = state.ContextMessages;

        Parallel.For(0, 100, index => state.AppendMessage(LlmMessage.User($"message-{index}")));

        Assert.Single(snapshot);
        Assert.Equal(101, state.ContextMessages.Count);
        Assert.Equal(100, state.ContextMessages.Skip(1).Select(message => message.Message.Content).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Exclusive_access_serializes_paired_conversation_state_and_memory_operations()
    {
        var state = new ConversationRuntimeState();
        var first = await state.AcquireExclusiveAccessAsync();
        var secondAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = Task.Run(async () =>
        {
            using (await state.AcquireExclusiveAccessAsync())
            {
                secondAcquired.SetResult();
            }
        });

        await Task.Delay(50);
        Assert.False(secondAcquired.Task.IsCompleted);

        first.Dispose();
        await second;
        Assert.True(secondAcquired.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Exclusive_access_is_shared_by_runtime_states_in_the_same_workspace_scope()
    {
        var scope = "workspace-" + Guid.NewGuid().ToString("N");
        var firstState = new ConversationRuntimeState(exclusiveAccessScope: scope);
        var secondState = new ConversationRuntimeState(exclusiveAccessScope: scope);
        var first = await firstState.AcquireExclusiveAccessAsync();
        var second = secondState.AcquireExclusiveAccessAsync();

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var acquired = await second;
    }

    [Fact]
    public void Synchronize_conversation_transcript_preserves_startup_context_and_replaces_stale_session_messages()
    {
        var state = new ConversationRuntimeState([LlmMessage.System("startup")]);
        state.AppendMessage(LlmMessage.User("stale"));

        state.SynchronizeConversationTranscript([LlmMessage.User("durable user"), LlmMessage.Assistant("durable assistant")]);

        Assert.Collection(
            state.ContextMessages,
            message => Assert.Equal(RuntimeContextSource.StartupContext, message.Source),
            message => Assert.Equal(RuntimeContextSource.RestoredConversationHistory, message.Source),
            message => Assert.Equal(RuntimeContextSource.RestoredConversationHistory, message.Source));
        Assert.Equal(["startup", "durable user", "durable assistant"], state.Messages.Select(message => message.Content));
    }

    [Fact]
    public void Replace_messages_validates_the_startup_boundary_and_describes_each_remaining_context_source()
    {
        var state = new ConversationRuntimeState();
        var message = LlmMessage.User("context");

        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReplaceMessages([message], startupContextCount: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReplaceMessages([message], startupContextCount: 2));

        state.ReplaceMessages([message], remainingSource: RuntimeContextSource.RestoredConversationHistory);
        Assert.Equal("Restored from conversation history at the user's request.", Assert.Single(state.ContextMessages).Detail);

        state.ReplaceMessages([message], remainingSource: RuntimeContextSource.CurrentTurnInput);
        Assert.Equal("Current user input being evaluated by the active loop before provider dispatch.", Assert.Single(state.ContextMessages).Detail);

        Assert.Throws<ArgumentOutOfRangeException>(() => state.ReplaceMessages([message], remainingSource: (RuntimeContextSource)int.MaxValue));
    }
}
