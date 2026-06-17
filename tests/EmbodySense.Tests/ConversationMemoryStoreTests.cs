using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Tests;

public sealed class ConversationMemoryStoreTests
{
    [Fact]
    public async Task AppendMessageAsync_writes_current_conversation_json_lines()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        await store.AppendMessageAsync(LlmMessage.User("hello memory"));
        await store.AppendMessageAsync(LlmMessage.Assistant("hello again"));

        Assert.True(File.Exists(paths.CurrentConversationPath));
        var text = await File.ReadAllTextAsync(paths.CurrentConversationPath);
        Assert.Contains("\"conversationId\":\"current\"", text);
        Assert.Contains("\"sequence\":1", text);
        Assert.Contains("\"role\":\"user\"", text);
        Assert.Contains("\"content\":\"hello again\"", text);
    }

    [Fact]
    public async Task LoadCurrentConversationAsync_restores_messages_in_sequence_order()
    {
        using var workspace = new TestWorkspace();
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));

        await store.AppendMessageAsync(LlmMessage.User("first"));
        await store.AppendMessageAsync(LlmMessage.Assistant("second"));

        var messages = await store.LoadCurrentConversationAsync();

        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal(LlmMessageRole.User, message.Role);
                Assert.Equal("first", message.Content);
            },
            message =>
            {
                Assert.Equal(LlmMessageRole.Assistant, message.Role);
                Assert.Equal("second", message.Content);
            });
    }

    [Fact]
    public async Task SearchCurrentConversationAsync_returns_matching_entries()
    {
        using var workspace = new TestWorkspace();
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));

        await store.AppendMessageAsync(LlmMessage.User("alpha planning detail"));
        await store.AppendMessageAsync(LlmMessage.Assistant("beta response"));

        var results = await store.SearchCurrentConversationAsync("planning");

        var result = Assert.Single(results);
        Assert.Equal(1, result.Sequence);
        Assert.Equal(LlmMessageRole.User, result.Role);
        Assert.Equal("alpha planning detail", result.Content);
    }
}
