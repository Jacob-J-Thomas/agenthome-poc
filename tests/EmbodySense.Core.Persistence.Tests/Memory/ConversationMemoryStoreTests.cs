using System.Text.Json;
using EmbodySense.Core.Application.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Memory;

public sealed class ConversationMemoryStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    [Fact]
    public async Task StartFreshConversationAsync_archives_existing_current_transcript_and_clears_current()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        await store.AppendMessageAsync(LlmMessage.User("old prompt"));

        await store.StartFreshConversationAsync();

        Assert.True(File.Exists(paths.CurrentConversationPath));
        Assert.Equal("", await File.ReadAllTextAsync(paths.CurrentConversationPath));
        var archivedPath = Assert.Single(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"));
        Assert.Contains("old prompt", await File.ReadAllTextAsync(archivedPath));
    }

    [Fact]
    public async Task ListConversationsAsync_returns_transcript_files_with_first_user_prompt()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        await WriteConversationAsync(
            paths,
            "saved-conversation",
            Entry("saved-conversation", 1, "assistant", "opening assistant note"),
            Entry("saved-conversation", 2, "user", "first saved prompt"));

        var conversations = await store.ListConversationsAsync();

        var conversation = Assert.Single(conversations);
        Assert.Equal("saved-conversation", conversation.ConversationId);
        Assert.Equal(2, conversation.MessageCount);
        Assert.Equal("first saved prompt", conversation.FirstPrompt);
        Assert.False(conversation.IsCurrent);
    }

    [Fact]
    public async Task ResumeConversationAsync_makes_selected_transcript_current_and_archives_previous_current()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        await store.AppendMessageAsync(LlmMessage.User("active prompt"));
        await WriteConversationAsync(
            paths,
            "saved-conversation",
            Entry("saved-conversation", 1, "user", "saved prompt"),
            Entry("saved-conversation", 2, "assistant", "saved answer"));

        await store.ResumeConversationAsync("saved-conversation");

        var messages = await store.LoadCurrentConversationAsync();
        Assert.Collection(
            messages,
            message =>
            {
                Assert.Equal(LlmMessageRole.User, message.Role);
                Assert.Equal("saved prompt", message.Content);
            },
            message =>
            {
                Assert.Equal(LlmMessageRole.Assistant, message.Role);
                Assert.Equal("saved answer", message.Content);
            });
        Assert.Contains("\"conversationId\":\"current\"", await File.ReadAllTextAsync(paths.CurrentConversationPath));
        Assert.Contains(Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson"), File.Exists);
    }

    [Fact]
    public async Task ListConversationsAsync_returns_archived_transcript_files()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        Directory.CreateDirectory(paths.ArchivedConversationMemoryPath);
        await WriteConversationAsync(
            paths,
            Path.Combine("archive", "20260618T0102030000000Z"),
            Entry("current", 1, "user", "archived prompt"));

        var conversation = Assert.Single(await store.ListConversationsAsync());

        Assert.Equal("archive/20260618T0102030000000Z", conversation.ConversationId);
        Assert.Equal("archived prompt", conversation.FirstPrompt);
    }

    [Fact]
    public async Task ResumeConversationAsync_loads_archived_transcript_files()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        await WriteConversationAsync(
            paths,
            Path.Combine("archive", "20260618T0102030000000Z"),
            Entry("current", 1, "user", "archived prompt"));

        await store.ResumeConversationAsync("archive/20260618T0102030000000Z");

        var message = Assert.Single(await store.LoadCurrentConversationAsync());
        Assert.Equal("archived prompt", message.Content);
        Assert.Contains("\"conversationId\":\"current\"", await File.ReadAllTextAsync(paths.CurrentConversationPath));
    }

    private static async Task WriteConversationAsync(
        WorkspacePaths paths,
        string conversationId,
        params ConversationMemoryEntry[] entries)
    {
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        var path = Path.Combine(paths.ConversationMemoryPath, conversationId + ".ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? paths.ConversationMemoryPath);
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static ConversationMemoryEntry Entry(string conversationId, int sequence, string role, string content)
    {
        return new ConversationMemoryEntry(1, conversationId, sequence, DateTimeOffset.Parse("2026-06-01T00:00:00+00:00").AddMinutes(sequence), role, content);
    }
}
