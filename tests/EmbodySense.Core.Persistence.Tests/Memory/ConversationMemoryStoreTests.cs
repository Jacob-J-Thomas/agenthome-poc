using EmbodySense.Core.Common.Inference;
using System.Text.Json;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Application.Memory.Models;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Memory;

public sealed class ConversationMemoryStoreTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

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
    public async Task LoadCurrentConversationAsync_requires_explicit_cleanup_for_the_superseded_identityless_shape()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        var legacyEntry = """{"schemaVersion":1,"conversationId":"current","sequence":1,"timestampUtc":"2026-07-31T00:00:00Z","role":"user","content":"legacy prompt"}""";
        await File.WriteAllTextAsync(paths.CurrentConversationPath, legacyEntry);
        var store = new ConversationMemoryStore(paths);

        var exception = await Assert.ThrowsAsync<ConversationTranscriptCleanupRequiredException>(() => store.LoadCurrentConversationSnapshotAsync());

        Assert.Equal(paths.CurrentConversationPath, exception.TranscriptPath);
        Assert.Contains("Back up and remove this transcript file", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Automatic migration", exception.Message, StringComparison.Ordinal);
        Assert.Equal(legacyEntry, await File.ReadAllTextAsync(paths.CurrentConversationPath));
    }

    [Fact]
    public async Task LoadConversationAsync_reads_current_saved_and_archived_transcripts_and_rejects_missing_ids()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("active prompt"));
        await WriteConversationAsync(paths, "saved-conversation", Entry("saved-conversation", 1, "assistant", "saved answer"));
        await WriteConversationAsync(paths, Path.Combine("archive", "20260618T0102030000000Z"), Entry("current", 1, "user", "archived prompt"));

        var current = await store.LoadConversationAsync("current");
        var saved = await store.LoadConversationAsync("saved-conversation");
        var archived = await store.LoadConversationAsync("archive/20260618T0102030000000Z");
        var missing = await Assert.ThrowsAsync<FileNotFoundException>(() => store.LoadConversationAsync("missing-conversation"));

        Assert.Equal("active prompt", Assert.Single(current).Content);
        Assert.Equal("saved answer", Assert.Single(saved).Content);
        Assert.Equal("archived prompt", Assert.Single(archived).Content);
        Assert.Contains("missing-conversation", missing.Message, StringComparison.Ordinal);
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
        var entries = (await File.ReadAllLinesAsync(paths.CurrentConversationPath)).Select(line => JsonSerializer.Deserialize<ConversationMemoryEntry>(line, _jsonOptions)!).ToArray();
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
        var expected = await first.LoadCurrentConversationSnapshotAsync();

        var results = await Task.WhenAll(
            first.TryAppendMessageAsync(expected.ConversationId, expected.Version, expected.Messages, LlmMessage.Assistant("winner-a")),
            second.TryAppendMessageAsync(expected.ConversationId, expected.Version, expected.Messages, LlmMessage.Assistant("winner-b")));

        Assert.Single(results, result => result);
        Assert.Single(results, result => !result);
        var messages = await first.LoadCurrentConversationAsync();
        Assert.Equal(2, messages.Count);
        Assert.Contains(messages[^1].Content, new[] { "winner-a", "winner-b" });
    }

    [Fact]
    public async Task Identity_bearing_publication_rejects_reuse_of_an_identity_from_the_expected_prefix()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("seed"));
        var expected = await store.LoadCurrentConversationSnapshotAsync();
        var existing = JsonSerializer.Deserialize<ConversationMemoryEntry>(Assert.Single(await File.ReadAllLinesAsync(paths.CurrentConversationPath)), _jsonOptions)!;

        var result = await store.TryPublishMessageAsync(
            expected.ConversationId,
            expected.Version,
            expected.Messages,
            new ConversationMessagePublication(existing.MessageId, "new-publication", LlmMessage.Assistant("must not append")));

        Assert.Equal(ConversationPublicationAppendStatus.Conflict, result.Status);
        Assert.Collection(await store.LoadCurrentConversationAsync(), message => Assert.Equal("seed", message.Content));
    }

    [Fact]
    public async Task Atomic_expected_prefix_append_refuses_to_race_an_existing_external_writer()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("seed"));
        var expected = await store.LoadCurrentConversationSnapshotAsync();
        await using var externalWriter = new FileStream(paths.CurrentConversationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        await Assert.ThrowsAsync<IOException>(() => store.TryAppendMessageAsync(expected.ConversationId, expected.Version, expected.Messages, LlmMessage.Assistant("must not race")));

        Assert.Single(await store.LoadCurrentConversationAsync());
    }

    [Fact]
    public async Task Atomic_append_rejects_an_identical_empty_prefix_after_the_logical_conversation_is_reset()
    {
        using var workspace = new TestWorkspace();
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));
        var captured = await store.LoadCurrentConversationSnapshotAsync();

        await store.StartFreshConversationAsync();
        var appended = await store.TryAppendMessageAsync(captured.ConversationId, captured.Version, captured.Messages, LlmMessage.Assistant("stale output"));
        var current = await store.LoadCurrentConversationSnapshotAsync();

        Assert.False(appended);
        Assert.NotEqual(captured.Version, current.Version);
        Assert.Empty(current.Messages);
    }

    [Fact]
    public async Task AppendMessageAsync_preserves_valid_ndjson_when_the_existing_final_line_has_no_newline()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath, JsonSerializer.Serialize(Entry("current", 1, "user", "seed"), _jsonOptions));
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
    public async Task ListConversationsAsync_waits_for_the_cross_process_current_conversation_lease()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("active prompt"));
        await using var externalLease = new FileStream(paths.CurrentConversationPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var listing = store.ListConversationsAsync();
        await Task.Delay(75);

        Assert.False(listing.IsCompleted);
        await externalLease.DisposeAsync();
        var conversation = Assert.Single(await listing.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(conversation.IsCurrent);
    }

    [Fact]
    public async Task LoadConversationHistorySnapshotAsync_waits_for_the_cross_process_lease_and_returns_complete_lines()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        await store.AppendMessageAsync(LlmMessage.User("snapshot prompt"));
        await store.AppendMessageAsync(LlmMessage.Assistant("snapshot answer"));
        await using var externalLease = new FileStream(paths.CurrentConversationPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var snapshotTask = store.LoadConversationHistorySnapshotAsync(50, 400, 4_000_000);
        await Task.Delay(75);

        Assert.False(snapshotTask.IsCompleted);
        await externalLease.DisposeAsync();
        var snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(2));
        var current = Assert.Single(snapshot.Transcripts);
        Assert.True(current.Exists);
        Assert.True(current.IsCurrent);
        Assert.Equal(2, current.Lines.Count);
        Assert.Contains("snapshot prompt", current.Lines[0], StringComparison.Ordinal);
        Assert.Contains("snapshot answer", current.Lines[1], StringComparison.Ordinal);
        Assert.False(current.AdditionalContentOmitted);
        Assert.False(snapshot.AdditionalFilesOmitted);
    }

    [Theory]
    [InlineData(0, 1, 1, "maxTranscriptFiles")]
    [InlineData(1, 0, 1, "maxLinesPerTranscript")]
    [InlineData(1, 1, 0, "maxTotalCharacters")]
    public async Task LoadConversationHistorySnapshotAsync_rejects_nonpositive_bounds(int maxTranscriptFiles, int maxLinesPerTranscript, int maxTotalCharacters, string parameterName)
    {
        using var workspace = new TestWorkspace();
        var store = new ConversationMemoryStore(new WorkspacePaths(workspace.RootPath));

        var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.LoadConversationHistorySnapshotAsync(maxTranscriptFiles, maxLinesPerTranscript, maxTotalCharacters));

        Assert.Equal(parameterName, exception.ParamName);
    }

    [Fact]
    public async Task LoadConversationHistorySnapshotAsync_enforces_the_file_bound_and_reports_omission()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteConversationAsync(paths, "saved-a", Entry("saved-a", 1, "user", "first saved"));
        await WriteConversationAsync(paths, "saved-b", Entry("saved-b", 1, "user", "second saved"));

        var snapshot = await new ConversationMemoryStore(paths).LoadConversationHistorySnapshotAsync(2, 400, 4_000_000);

        Assert.Collection(
            snapshot.Transcripts,
            current =>
            {
                Assert.True(current.IsCurrent);
                Assert.False(current.Exists);
            },
            saved => Assert.Equal("saved-a", saved.ConversationId));
        Assert.True(snapshot.AdditionalFilesOmitted);
    }

    [Fact]
    public async Task LoadConversationHistorySnapshotAsync_bounds_retained_lines_and_aggregate_characters()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteConversationAsync(
            paths,
            "saved-a",
            Entry("saved-a", 1, "user", "first"),
            Entry("saved-a", 2, "assistant", "second"),
            Entry("saved-a", 3, "user", "third"));
        await WriteConversationAsync(paths, "saved-b", Entry("saved-b", 1, "user", "later"));

        var lineBound = await new ConversationMemoryStore(paths).LoadConversationHistorySnapshotAsync(3, 2, 4_000);
        var characterBound = await new ConversationMemoryStore(paths).LoadConversationHistorySnapshotAsync(3, 20, 30);

        var lineBoundSaved = Assert.Single(lineBound.Transcripts, transcript => transcript.ConversationId == "saved-a");
        Assert.Equal(2, lineBoundSaved.Lines.Count);
        Assert.True(lineBoundSaved.AdditionalContentOmitted);
        Assert.Contains(characterBound.Transcripts, transcript => transcript.AdditionalContentOmitted);
        Assert.InRange(characterBound.Transcripts.Sum(transcript => transcript.Lines.Sum(line => line.Length)), 0, 30);
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

    [Fact]
    public async Task Empty_and_invalid_public_boundaries_are_deterministic_and_fail_closed()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);

        var emptyHistory = await store.LoadConversationHistorySnapshotAsync(1, 1, 1);
        var current = Assert.Single(emptyHistory.Transcripts);
        Assert.True(current.IsCurrent);
        Assert.False(current.Exists);
        Assert.Empty(await store.ListConversationsAsync());
        await store.ResumeConversationAsync("current");
        await Assert.ThrowsAsync<FileNotFoundException>(() => store.ResumeConversationAsync("missing"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.SearchCurrentConversationAsync("query", 0));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadConversationAsync("nested/path"));
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadConversationAsync(".."));

        await WriteConversationAsync(paths, "normalized", Entry("normalized", 1, "user", "normalized"));
        Assert.Equal("normalized", Assert.Single(await store.LoadConversationAsync("normalized.ndjson")).Content);
    }

    [Fact]
    public async Task History_snapshot_reports_archive_and_exact_content_bounds_without_losing_complete_lines()
    {
        using var archiveWorkspace = new TestWorkspace();
        var archivePaths = new WorkspacePaths(archiveWorkspace.RootPath);
        await WriteConversationAsync(archivePaths, Path.Combine("archive", "archive-a"), Entry("current", 1, "user", "first"));
        await WriteConversationAsync(archivePaths, Path.Combine("archive", "archive-b"), Entry("current", 1, "user", "second"));
        var archiveStore = new ConversationMemoryStore(archivePaths);

        var boundedArchives = await archiveStore.LoadConversationHistorySnapshotAsync(2, 10, 1_000);
        Assert.True(boundedArchives.AdditionalFilesOmitted);
        Assert.Equal(2, boundedArchives.Transcripts.Count);
        Assert.StartsWith("archive/", boundedArchives.Transcripts[1].ConversationId, StringComparison.Ordinal);
        Assert.True((await archiveStore.LoadConversationHistorySnapshotAsync(1, 10, 1_000)).AdditionalFilesOmitted);

        using var contentWorkspace = new TestWorkspace();
        var contentPaths = new WorkspacePaths(contentWorkspace.RootPath);
        Directory.CreateDirectory(contentPaths.ConversationMemoryPath);
        await File.WriteAllTextAsync(Path.Combine(contentPaths.ConversationMemoryPath, "unterminated.ndjson"), "abc");
        await File.WriteAllTextAsync(Path.Combine(contentPaths.ConversationMemoryPath, "crlf.ndjson"), "a\r\nb\r\n");
        var contentStore = new ConversationMemoryStore(contentPaths);

        var complete = await contentStore.LoadConversationHistorySnapshotAsync(3, 10, 1_000);
        Assert.Equal("abc", Assert.Single(Assert.Single(complete.Transcripts, item => item.ConversationId == "unterminated").Lines));
        Assert.Equal(["a", "b"], Assert.Single(complete.Transcripts, item => item.ConversationId == "crlf").Lines);
        using var characterWorkspace = new TestWorkspace();
        var characterPaths = new WorkspacePaths(characterWorkspace.RootPath);
        Directory.CreateDirectory(characterPaths.ConversationMemoryPath);
        await File.WriteAllTextAsync(Path.Combine(characterPaths.ConversationMemoryPath, "exact-characters.ndjson"), "abc");
        var exactCharacters = await new ConversationMemoryStore(characterPaths).LoadConversationHistorySnapshotAsync(2, 10, 3);
        var exactTranscript = Assert.Single(exactCharacters.Transcripts, item => item.ConversationId == "exact-characters");
        Assert.False(exactTranscript.AdditionalContentOmitted);

        using var lineWorkspace = new TestWorkspace();
        var linePaths = new WorkspacePaths(lineWorkspace.RootPath);
        Directory.CreateDirectory(linePaths.ConversationMemoryPath);
        await File.WriteAllTextAsync(Path.Combine(linePaths.ConversationMemoryPath, "exact-lines.ndjson"), "a\nb\n");
        var exactLines = await new ConversationMemoryStore(linePaths).LoadConversationHistorySnapshotAsync(2, 2, 1_000);
        var lines = Assert.Single(exactLines.Transcripts, item => item.ConversationId == "exact-lines");
        Assert.Equal(["a", "b"], lines.Lines);
        Assert.False(lines.AdditionalContentOmitted);

        using var overflowWorkspace = new TestWorkspace();
        var overflowPaths = new WorkspacePaths(overflowWorkspace.RootPath);
        Directory.CreateDirectory(overflowPaths.ConversationMemoryPath);
        await File.WriteAllTextAsync(Path.Combine(overflowPaths.ConversationMemoryPath, "overflow.ndjson"), new string('x', 4_097));
        var overflow = await new ConversationMemoryStore(overflowPaths).LoadConversationHistorySnapshotAsync(2, 10, 4_096);
        Assert.True(Assert.Single(overflow.Transcripts, item => item.ConversationId == "overflow").AdditionalContentOmitted);
    }

    [Fact]
    public async Task Listing_orders_current_and_saved_conversations_by_latest_activity()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteConversationAsync(paths, "older", Entry("older", 1, "user", "older"));
        await WriteConversationAsync(paths, "newer", Entry("newer", 2, "user", "newer"));
        var store = new ConversationMemoryStore(paths);

        var conversations = await store.ListConversationsAsync();

        Assert.Equal(["newer", "older"], conversations.Select(item => item.ConversationId));
    }

    [Fact]
    public async Task Transcript_and_identity_validation_rejects_unsupported_or_ambiguous_persisted_state()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var store = new ConversationMemoryStore(paths);
        var unsupported = Entry("unsupported", 1, "user", "content") with { SchemaVersion = 2 };
        await WriteConversationAsync(paths, "unsupported", unsupported);
        await WriteConversationAsync(paths, "unknown-role", Entry("unknown-role", 1, "unknown", "content"));
        var duplicate = Entry("duplicate", 1, "user", "first");
        await WriteConversationAsync(paths, "duplicate", duplicate, duplicate with { Sequence = 2, Content = "second" });

        await Assert.ThrowsAsync<FormatException>(() => store.LoadConversationAsync("unsupported"));
        await Assert.ThrowsAsync<FormatException>(() => store.LoadConversationAsync("unknown-role"));
        await Assert.ThrowsAsync<FormatException>(() => store.LoadConversationAsync("duplicate"));

        Directory.CreateDirectory(paths.ConversationMemoryPath);
        await File.WriteAllTextAsync(paths.CurrentConversationPath + ".identity.json", "{\"schemaVersion\":1,\"conversationId\":\"current\",\"version\":\"short\"}");
        await Assert.ThrowsAsync<FormatException>(() => store.LoadCurrentConversationSnapshotAsync());
    }

    private static async Task WriteConversationAsync(
        WorkspacePaths paths,
        string conversationId,
        params ConversationMemoryEntry[] entries)
    {
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        var path = Path.Combine(paths.ConversationMemoryPath, conversationId + ".ndjson");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? paths.ConversationMemoryPath);
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, _jsonOptions));
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private static ConversationMemoryEntry Entry(string conversationId, int sequence, string role, string content)
    {
        return new ConversationMemoryEntry(1, conversationId, sequence, DateTimeOffset.Parse("2026-06-01T00:00:00+00:00").AddMinutes(sequence), $"message-{sequence}", $"publication-{sequence}", role, content);
    }
}
