using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Startup.Runtime;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Runtime;

public sealed class ConversationTranscriptReaderTests
{
    [Fact]
    public async Task ReadCurrentAsync_returns_the_canonical_durable_transcript_without_an_inference_runtime()
    {
        using var workspace = new TestWorkspace();
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        await store.AppendMessageAsync(LlmMessage.User("durable prompt"));
        await store.AppendMessageAsync(LlmMessage.Assistant("durable answer"));

        var transcript = await new ConversationTranscriptReader().ReadCurrentAsync(workspace.RootPath);

        Assert.Collection(
            transcript,
            message =>
            {
                Assert.Equal("User", message.Role);
                Assert.Equal("durable prompt", message.Content);
            },
            message =>
            {
                Assert.Equal("Assistant", message.Role);
                Assert.Equal("durable answer", message.Content);
            });
    }

    [Fact]
    public async Task ReadCurrentAsync_rejects_a_missing_workspace_root()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new ConversationTranscriptReader().ReadCurrentAsync(" "));
    }
}
