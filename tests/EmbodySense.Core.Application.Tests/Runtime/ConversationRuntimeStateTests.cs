using EmbodySense.Core.Application.Runtime.State;
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
}
