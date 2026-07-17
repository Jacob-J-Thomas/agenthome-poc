using System.Text.Json;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;
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
    public async Task Concurrent_appends_from_distinct_store_instances_commit_unique_contiguous_sequences()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new ConversationMemoryStore(paths);
        var second = new ConversationMemoryStore(paths);

        await Task.WhenAll(Enumerable.Range(1, 40).Select(index => (index % 2 == 0 ? first : second).AppendMessageAsync(LlmMessage.User($"message-{index}"))));

        var messages = await first.LoadCurrentConversationAsync();
        Assert.Equal(40, messages.Count);
        Assert.Equal(40, messages.Select(message => message.Content).Distinct(StringComparer.Ordinal).Count());
        var entries = (await File.ReadAllLinesAsync(paths.CurrentConversationPath)).Select(line => JsonSerializer.Deserialize<ConversationMemoryEntry>(line, JsonOptions)!).ToArray();
        Assert.Equal(Enumerable.Range(1, 40), entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public async Task Atomic_expected_prefix_append_has_exactly_one_winner_across_store_instances()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var first = new ConversationMemoryStore(paths);
        var second = new ConversationMemoryStore(paths);
        await first.AppendMessageAsync(LlmMessage.User("seed"));
        var expected = await first.LoadCurrentConversationAsync();

        var results = await Task.WhenAll(
            first.TryAppendMessageAsync(expected, LlmMessage.Assistant("winner-a")),
            second.TryAppendMessageAsync(expected, LlmMessage.Assistant("winner-b")));

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        var messages = await first.LoadCurrentConversationAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages[^1].Content, new[] { "winner-a", "winner-b" });
    }

    [Fact]
    public async Task Atomic_expected_prefix_append_refuses_to_race_an_existing_external_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("seed"));
        var expected = await store.LoadCurrentConversationAsync();
        await using var externalWriter = new FileStream(paths.CurrentConversationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        await Assert.ThrowsAsync<IOException>(() => store.TryAppendMessageAsync(expected, LlmMessage.Assistant("must not race")));

        Assert.Single(await store.LoadCurrentConversationAsync());
    }

    [Fact]
    public async Task AppendMessageAsync_preserves_valid_ndjson_when_the_existing_final_line_has_no_newline()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath, JsonSerializer.Serialize(Entry("current", 1, "user", "seed"), JsonOptions));
        var store = new ConversationMemoryStore(paths);

        await store.AppendMessageAsync(LlmMessage.Assistant("second"));

        Assert.Collection(
            await store.LoadCurrentConversationAsync(),
            message => Assert.Equal("seed", message.Content),
            message => Assert.Equal("second", message.Content));
        Assert.Equal(2, (await File.ReadAllLinesAsync(paths.CurrentConversationPath)).Length);
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
