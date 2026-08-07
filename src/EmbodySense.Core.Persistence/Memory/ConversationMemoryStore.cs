using EmbodySense.Core.Common.Inference;
using EmbodySense.Core.Application.Memory.Models;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Memory.Models;

namespace EmbodySense.Core.Persistence.Memory;

/// <summary>
/// Persists the current and archived conversation transcripts using strict version-1 newline-delimited JSON.
/// </summary>
/// <remarks>
/// Current-conversation mutations hold a process-local gate and an exclusive cross-process file lease. Creating or rotating a
/// conversation atomically replaces identity metadata before its transcript is created, cleared, or replaced. Because identity
/// and transcript changes are separate file commits, cancellation or I/O failure can leave the new identity beside the prior or
/// missing transcript. First append also creates missing identity metadata before appending, while a failed transcript append
/// restores the prior file length. Compare-and-append verifies conversation identity, version, and exact prefix under the same
/// lease. Syntactically malformed JSON can throw <see cref="JsonException"/>; unsupported schemas, semantically invalid entries,
/// invalid roles, or invalid identity fields throw <see cref="FormatException"/>. No migration or legacy alias is attempted.
/// </remarks>
public sealed class ConversationMemoryStore : IConversationMemoryStore
{
    private const int SchemaVersion = 1;
    private const int IdentitySchemaVersion = 1;
    private const string CurrentConversationId = "current";
    private const string ArchiveDirectoryName = "archive";
    private static readonly TimeSpan _currentConversationLeaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _currentConversationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkspacePaths _paths;
    private readonly SemaphoreSlim _currentConversationGate;
    private string CurrentConversationIdentityPath => _paths.CurrentConversationPath + ".identity.json";
    private string CurrentConversationLockPath => _paths.CurrentConversationPath + ".lock";

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationMemoryStore"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    public ConversationMemoryStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _currentConversationGate = _currentConversationGates.GetOrAdd(Path.GetFullPath(paths.CurrentConversationPath), _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Loads the validated current transcript in persisted message order.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the LLM messages.</returns>
    public async Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default)
    {
        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadCurrentConversationSnapshotUnsafeAsync(cancellationToken)).Messages;
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <summary>
    /// Loads the current transcript together with its stable identity and content version.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the conversation memory snapshot.</returns>
    public async Task<ConversationMemorySnapshot> LoadCurrentConversationSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCurrentConversationSnapshotUnsafeAsync(cancellationToken);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <summary>
    /// Lists the current and archived transcripts in deterministic recency order.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task whose result is the conversation transcript list items.</returns>
    public async Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default)
    {
        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            return await ListConversationsUnsafeAsync(cancellationToken);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <summary>
    /// Reads a bounded, internally consistent transcript-file snapshot while holding the same in-process gate and cross-process lease used by current-conversation writes and rotation.
    /// </summary>
    /// <param name="maxTranscriptFiles">The maximum number of file snapshots to return, including the current transcript placeholder.</param>
    /// <param name="maxLinesPerTranscript">The maximum number of complete lines retained for one transcript.</param>
    /// <param name="maxTotalCharacters">The aggregate character budget shared by every retained transcript line.</param>
    /// <param name="cancellationToken">Cancels lease acquisition and file reads.</param>
    /// <returns>A detached snapshot that configuration readers can parse after the persistence lease is released.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a configured bound is not positive.</exception>
    /// <exception cref="OperationCanceledException">Thrown when <paramref name="cancellationToken"/> is cancelled.</exception>
    public async Task<ConversationHistorySnapshot> LoadConversationHistorySnapshotAsync(
        int maxTranscriptFiles,
        int maxLinesPerTranscript,
        int maxTotalCharacters,
        CancellationToken cancellationToken = default)
    {
        if (maxTranscriptFiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTranscriptFiles), maxTranscriptFiles, "Maximum transcript files must be greater than zero.");
        }

        if (maxLinesPerTranscript <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLinesPerTranscript), maxLinesPerTranscript, "Maximum transcript lines must be greater than zero.");
        }

        if (maxTotalCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalCharacters), maxTotalCharacters, "Maximum transcript characters must be greater than zero.");
        }

        if (!Directory.Exists(_paths.ConversationMemoryPath))
        {
            return new ConversationHistorySnapshot(
                [new ConversationTranscriptFileSnapshot(CurrentConversationId, _paths.CurrentConversationPath, true, false, [], false)],
                false);
        }

        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            // Current is always represented first, even when its file is absent. That stable slot lets
            // configuration clients distinguish a fresh conversation from a truncated file inventory.
            var candidates = new List<(string ConversationId, string Path, bool IsCurrent)>
            {
                (CurrentConversationId, _paths.CurrentConversationPath, true)
            };
            var additionalFilesOmitted = false;

            foreach (var path in Directory.EnumerateFiles(_paths.ConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly).Where(path => !SamePath(path, _paths.CurrentConversationPath)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (candidates.Count == maxTranscriptFiles)
                {
                    additionalFilesOmitted = true;
                    break;
                }

                candidates.Add((Path.GetFileNameWithoutExtension(path), path, false));
            }

            if (candidates.Count < maxTranscriptFiles && Directory.Exists(_paths.ArchivedConversationMemoryPath))
            {
                foreach (var path in Directory.EnumerateFiles(_paths.ArchivedConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc))
                {
                    if (candidates.Count == maxTranscriptFiles)
                    {
                        additionalFilesOmitted = true;
                        break;
                    }

                    candidates.Add(($"{ArchiveDirectoryName}/{Path.GetFileNameWithoutExtension(path)}", path, false));
                }
            }
            else if (Directory.Exists(_paths.ArchivedConversationMemoryPath) && Directory.EnumerateFiles(_paths.ArchivedConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly).Any())
            {
                additionalFilesOmitted = true;
            }

            var transcripts = new List<ConversationTranscriptFileSnapshot>(candidates.Count);
            var remainingCharacters = maxTotalCharacters;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exists = File.Exists(candidate.Path);
                IReadOnlyList<string> lines = [];
                var additionalContentOmitted = false;
                if (exists)
                {
                    var read = await ReadTranscriptLinesAsync(candidate.Path, maxLinesPerTranscript, remainingCharacters, cancellationToken);
                    lines = read.Lines;
                    remainingCharacters -= read.CharactersRead;
                    additionalContentOmitted = read.AdditionalContentOmitted;
                }

                transcripts.Add(new ConversationTranscriptFileSnapshot(candidate.ConversationId, candidate.Path, candidate.IsCurrent, exists, lines, additionalContentOmitted));
            }

            return new ConversationHistorySnapshot(transcripts.ToArray(), additionalFilesOmitted);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    private async Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_paths.ConversationMemoryPath))
        {
            return [];
        }

        var listItems = new List<ConversationTranscriptListItem>();
        foreach (var path in EnumerateConversationFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = await LoadEntriesAsync(path, cancellationToken);
            if (entries.Count == 0)
            {
                continue;
            }

            var conversationId = GetConversationId(path);
            var firstPrompt = entries.FirstOrDefault(entry => IsRole(entry, LlmMessageRole.User))?.Content;
            listItems.Add(new ConversationTranscriptListItem(
                conversationId,
                entries.Count,
                entries[0].TimestampUtc,
                entries[^1].TimestampUtc,
                firstPrompt,
                IsCurrentConversationId(conversationId)));
        }

        return listItems
            .OrderByDescending(item => item.IsCurrent)
            .ThenByDescending(item => item.LastTimestampUtc)
            .ThenBy(item => item.ConversationId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Archives the current transcript when nonempty, atomically replaces its identity metadata, and clears the current transcript.
    /// </summary>
    /// <remarks>
    /// Archive copy, identity replacement, and transcript clearing are separate file commits under one
    /// cross-process lease. Cancellation or I/O failure may leave an archive copy or new identity in place;
    /// the operation does not claim a multi-file transaction.
    /// </remarks>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes after the fresh identity and empty current transcript have been written.</returns>
    public async Task StartFreshConversationAsync(CancellationToken cancellationToken = default)
    {
        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            if (File.Exists(_paths.CurrentConversationPath) && new FileInfo(_paths.CurrentConversationPath).Length > 0)
            {
                await ArchiveCurrentConversationAsync(cancellationToken);
            }

            await WriteCurrentConversationIdentityAsync(CreateCurrentConversationIdentity(), cancellationToken);
            await File.WriteAllTextAsync(_paths.CurrentConversationPath, string.Empty, cancellationToken);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <summary>
    /// Loads the current or archived transcript identified by <paramref name="conversationId"/>.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The validated messages in persisted sequence order.</returns>
    public async Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var normalizedConversationId = NormalizeConversationId(conversationId);
        if (IsCurrentConversationId(normalizedConversationId))
        {
            return await LoadCurrentConversationAsync(cancellationToken);
        }

        var path = GetConversationPath(normalizedConversationId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Conversation `{conversationId}` was not found.", path);
        }

        var entries = await LoadEntriesAsync(path, cancellationToken);
        return entries.Select(ToMessage).ToArray();
    }

    /// <summary>
    /// Archives any nonempty current transcript, atomically replaces its identity metadata, and copies an archived transcript into the current file.
    /// </summary>
    /// <remarks>
    /// Archive copy, identity replacement, and current-transcript replacement are separate file commits
    /// under one cross-process lease. Cancellation or I/O failure can leave earlier commits in place.
    /// </remarks>
    /// <param name="conversationId">The conversation ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes after the new identity and copied current transcript have been written.</returns>
    public async Task ResumeConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var normalizedConversationId = NormalizeConversationId(conversationId);
        if (IsCurrentConversationId(normalizedConversationId))
        {
            return;
        }

        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            var sourcePath = GetConversationPath(normalizedConversationId);
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Conversation `{conversationId}` was not found.", sourcePath);
            }

            var sourceEntries = await LoadEntriesAsync(sourcePath, cancellationToken);
            if (File.Exists(_paths.CurrentConversationPath) && new FileInfo(_paths.CurrentConversationPath).Length > 0)
            {
                await ArchiveCurrentConversationAsync(cancellationToken);
            }

            await WriteCurrentConversationIdentityAsync(CreateCurrentConversationIdentity(), cancellationToken);
            await WriteEntriesAsync(_paths.CurrentConversationPath, sourceEntries.Select(entry => entry with { ConversationId = CurrentConversationId }), cancellationToken);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <summary>
    /// Appends one version-1 message while holding current-conversation ownership.
    /// </summary>
    /// <remarks>
    /// Ordinary appends do not change the conversation-generation identity established when the current conversation is created or resumed.
    /// </remarks>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public async Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            await AppendMessageUnsafeAsync(message, cancellationToken);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <summary>
    /// Appends only when the current conversation identity, content version, and exact message prefix match the caller's snapshot.
    /// </summary>
    /// <param name="expectedConversationId">The expected conversation ID.</param>
    /// <param name="expectedConversationVersion">The expected conversation version.</param>
    /// <param name="expectedPrefix">The expected prefix.</param>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// <see langword="true"/> after the append succeeds; <see langword="false"/> when any expected identity, version, or
    /// message-prefix value is stale. A successful append does not change the conversation-generation identity.
    /// </returns>
    public async Task<bool> TryAppendMessageAsync(string expectedConversationId, string expectedConversationVersion, IReadOnlyList<LlmMessage> expectedPrefix, LlmMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConversationVersion);
        ArgumentNullException.ThrowIfNull(expectedPrefix);
        ArgumentNullException.ThrowIfNull(message);

        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            // FileShare.None makes the prefix comparison and append one file-level critical section;
            // the outer lease extends that ownership to cooperating writers in other processes.
            await using var stream = OpenCurrentConversationForAtomicAppend();
            var currentEntries = await LoadEntriesAsync(stream, _paths.CurrentConversationPath, cancellationToken);
            var identity = await LoadOrCreateCurrentConversationIdentityAsync(cancellationToken);
            var current = currentEntries.Select(ToMessage).ToArray();
            var matches = string.Equals(identity.ConversationId, expectedConversationId, StringComparison.Ordinal)
                && string.Equals(identity.Version, expectedConversationVersion, StringComparison.Ordinal)
                && current.Length == expectedPrefix.Count
                && current.Zip(expectedPrefix).All(pair => pair.First.Role == pair.Second.Role && string.Equals(pair.First.Content, pair.Second.Content, StringComparison.Ordinal));
            if (!matches)
            {
                return false;
            }

            _ = await AppendMessageAsync(stream, message, currentEntries, CreateMessageId(), CreatePublicationId(), cancellationToken);
            return true;
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ConversationPublicationAppendResult> TryPublishMessageAsync(
        string expectedConversationId,
        string expectedConversationVersion,
        IReadOnlyList<LlmMessage> expectedPrefix,
        ConversationMessagePublication publication,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedConversationVersion);
        ArgumentNullException.ThrowIfNull(expectedPrefix);
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.MessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publication.PublicationId);
        ArgumentNullException.ThrowIfNull(publication.Message);

        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            await using var stream = OpenCurrentConversationForAtomicAppend();
            var currentEntries = await LoadEntriesAsync(stream, _paths.CurrentConversationPath, cancellationToken);
            var identity = await LoadOrCreateCurrentConversationIdentityAsync(cancellationToken);
            var currentMessages = currentEntries.Select(ToMessage).ToArray();
            var identityMatches = string.Equals(identity.ConversationId, expectedConversationId, StringComparison.Ordinal)
                && string.Equals(identity.Version, expectedConversationVersion, StringComparison.Ordinal);
            var prefixMatches = currentMessages.Length >= expectedPrefix.Count
                && currentMessages.Take(expectedPrefix.Count).Zip(expectedPrefix).All(pair => MessagesEqual(pair.First, pair.Second));
            if (!identityMatches || !prefixMatches)
            {
                return Result(ConversationPublicationAppendStatus.Conflict, identity, currentMessages);
            }

            if (currentEntries.Take(expectedPrefix.Count).Any(entry => string.Equals(entry.MessageId, publication.MessageId, StringComparison.Ordinal)
                || string.Equals(entry.PublicationId, publication.PublicationId, StringComparison.Ordinal)))
            {
                return Result(ConversationPublicationAppendStatus.Conflict, identity, currentMessages);
            }

            if (currentMessages.Length == expectedPrefix.Count)
            {
                _ = await AppendMessageAsync(stream, publication.Message, currentEntries, publication.MessageId, publication.PublicationId, cancellationToken);
                return Result(ConversationPublicationAppendStatus.Appended, identity, [.. currentMessages, publication.Message]);
            }

            if (currentMessages.Length == expectedPrefix.Count + 1)
            {
                var existing = currentEntries[expectedPrefix.Count];
                if (string.Equals(existing.MessageId, publication.MessageId, StringComparison.Ordinal)
                    && string.Equals(existing.PublicationId, publication.PublicationId, StringComparison.Ordinal)
                    && MessagesEqual(currentMessages[^1], publication.Message))
                {
                    return Result(ConversationPublicationAppendStatus.AlreadyPresent, identity, currentMessages);
                }
            }

            return Result(ConversationPublicationAppendStatus.Conflict, identity, currentMessages);
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    private async Task AppendMessageUnsafeAsync(LlmMessage message, CancellationToken cancellationToken)
    {
        _ = await LoadOrCreateCurrentConversationIdentityAsync(cancellationToken);
        await using var stream = OpenCurrentConversationForAtomicAppend();
        var entries = await LoadEntriesAsync(stream, _paths.CurrentConversationPath, cancellationToken);
        _ = await AppendMessageAsync(stream, message, entries, CreateMessageId(), CreatePublicationId(), cancellationToken);
    }

    private FileStream OpenCurrentConversationForAtomicAppend()
    {
        return new FileStream(_paths.CurrentConversationPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
    }

    private async Task<FileStream> AcquireCurrentConversationLeaseAsync(CancellationToken cancellationToken)
    {
        // A lock file is retained between owners; exclusive sharing, not file existence, represents ownership.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(CurrentConversationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(_currentConversationLeaseRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task<ConversationMemoryEntry> AppendMessageAsync(
        FileStream stream,
        LlmMessage message,
        IReadOnlyList<ConversationMemoryEntry> entries,
        string messageId,
        string publicationId,
        CancellationToken cancellationToken)
    {
        var sequence = entries.Count == 0 ? 1 : entries.Max(entry => entry.Sequence) + 1;
        var entry = new ConversationMemoryEntry(
            SchemaVersion,
            CurrentConversationId,
            sequence,
            DateTimeOffset.UtcNow,
            messageId,
            publicationId,
            message.Role.ToString().ToLowerInvariant(),
            message.Content);
        var line = JsonSerializer.Serialize(entry, _jsonOptions) + Environment.NewLine;
        stream.Position = stream.Length;
        if (stream.Length > 0)
        {
            stream.Position--;
            var lastByte = stream.ReadByte();
            stream.Position = stream.Length;
            if (lastByte != '\n')
            {
                line = Environment.NewLine + line;
            }
        }

        var originalLength = stream.Length;
        try
        {
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024, leaveOpen: true))
            {
                await writer.WriteAsync(line.AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            stream.Flush(flushToDisk: true);
            return entry;
        }
        catch
        {
            stream.SetLength(originalLength);
            stream.Position = originalLength;
            stream.Flush(flushToDisk: true);
            throw;
        }
    }

    private static ConversationPublicationAppendResult Result(ConversationPublicationAppendStatus status, CurrentConversationIdentity identity, IReadOnlyList<LlmMessage> messages)
    {
        return new ConversationPublicationAppendResult(status, new ConversationMemorySnapshot(identity.ConversationId, identity.Version, messages));
    }

    private static bool MessagesEqual(LlmMessage left, LlmMessage right)
    {
        return left.Role == right.Role && string.Equals(left.Content, right.Content, StringComparison.Ordinal);
    }

    private static string CreateMessageId()
    {
        return "message-" + Guid.NewGuid().ToString("N");
    }

    private static string CreatePublicationId()
    {
        return "publication-" + Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Searches the current transcript for a case-insensitive literal content substring.
    /// </summary>
    /// <param name="query">The nonblank literal substring to match.</param>
    /// <param name="limit">The positive maximum number of matches.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The first matching transcript entries in persisted sequence order.</returns>
    public async Task<IReadOnlyList<ConversationMemorySearchResult>> SearchCurrentConversationAsync(
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be greater than zero.");
        }

        await _currentConversationGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.ConversationMemoryPath);
            await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
            var entries = await LoadCurrentEntriesAsync(cancellationToken);
            return entries
                .Where(entry => entry.Content.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .Select(entry => new ConversationMemorySearchResult(
                    entry.ConversationId,
                    entry.Sequence,
                    entry.TimestampUtc,
                    ParseRole(entry.Role),
                    entry.Content))
                .ToArray();
        }
        finally
        {
            _currentConversationGate.Release();
        }
    }

    private async Task<ConversationMemorySnapshot> LoadCurrentConversationSnapshotUnsafeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ConversationMemoryPath);
        await using var lease = await AcquireCurrentConversationLeaseAsync(cancellationToken);
        var entries = await LoadCurrentEntriesAsync(cancellationToken);
        var identity = await LoadOrCreateCurrentConversationIdentityAsync(cancellationToken);
        return new ConversationMemorySnapshot(identity.ConversationId, identity.Version, entries.Select(ToMessage).ToArray());
    }

    private async Task<CurrentConversationIdentity> LoadOrCreateCurrentConversationIdentityAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CurrentConversationIdentityPath))
        {
            var created = CreateCurrentConversationIdentity();
            await WriteCurrentConversationIdentityAsync(created, cancellationToken);
            return created;
        }

        var json = await File.ReadAllTextAsync(CurrentConversationIdentityPath, cancellationToken);
        var identity = JsonSerializer.Deserialize<CurrentConversationIdentity>(json, _jsonOptions)
            ?? throw new FormatException("Current conversation identity metadata was empty.");
        if (identity.SchemaVersion != IdentitySchemaVersion
            || !string.Equals(identity.ConversationId, CurrentConversationId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(identity.Version)
            || identity.Version.Length != 64
            || identity.Version.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new FormatException("Current conversation identity metadata was invalid.");
        }

        return identity;
    }

    private async Task WriteCurrentConversationIdentityAsync(CurrentConversationIdentity identity, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ConversationMemoryPath);
        var temporaryPath = CurrentConversationIdentityPath + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            // Publish complete JSON through one same-directory rename so readers never observe a partially
            // written identity file. This does not make the separate transcript update transactional.
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(identity, _jsonOptions), cancellationToken);
            File.Move(temporaryPath, CurrentConversationIdentityPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static CurrentConversationIdentity CreateCurrentConversationIdentity()
    {
        var version = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return new CurrentConversationIdentity(IdentitySchemaVersion, CurrentConversationId, version);
    }

    private async Task<IReadOnlyList<ConversationMemoryEntry>> LoadCurrentEntriesAsync(CancellationToken cancellationToken)
    {
        return await LoadEntriesAsync(_paths.CurrentConversationPath, cancellationToken);
    }

    private async Task<IReadOnlyList<ConversationMemoryEntry>> LoadEntriesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await LoadEntriesAsync(stream, path, cancellationToken);
    }

    private static async Task<(IReadOnlyList<string> Lines, int CharactersRead, bool AdditionalContentOmitted)> ReadTranscriptLinesAsync(
        string path,
        int maxLines,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (maxCharacters == 0)
        {
            return ([], 0, stream.Length > 0);
        }

        var lines = new List<string>();
        var currentLine = new StringBuilder(Math.Min(maxCharacters, 4_096));
        var buffer = new char[4_096];
        var charactersRead = 0;
        while (lines.Count < maxLines && charactersRead < maxCharacters)
        {
            var allowed = maxCharacters - charactersRead;
            // Read one character beyond the remaining budget when the buffer permits. That probe
            // distinguishes exact exhaustion from a complete file without retaining over-budget text.
            var requested = allowed >= buffer.Length ? buffer.Length : allowed + 1;
            var count = await reader.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (count == 0)
            {
                if (currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                }

                return (lines.ToArray(), charactersRead, false);
            }

            var retainedCount = Math.Min(count, allowed);
            charactersRead += retainedCount;
            var processedCount = 0;
            for (var index = 0; index < retainedCount && lines.Count < maxLines; index++)
            {
                processedCount++;
                var character = buffer[index];
                if (character == '\n')
                {
                    if (currentLine.Length > 0 && currentLine[^1] == '\r')
                    {
                        currentLine.Length--;
                    }

                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                }
                else
                {
                    currentLine.Append(character);
                }
            }

            if (count > retainedCount || processedCount < retainedCount)
            {
                return (lines.ToArray(), charactersRead, true);
            }

            if (lines.Count == maxLines)
            {
                var extra = new char[1];
                return (lines.ToArray(), charactersRead, await reader.ReadAsync(extra, cancellationToken) > 0);
            }
        }

        if (charactersRead == maxCharacters)
        {
            var extra = new char[1];
            if (await reader.ReadAsync(extra, cancellationToken) == 0)
            {
                if (currentLine.Length > 0 && lines.Count < maxLines)
                {
                    lines.Add(currentLine.ToString());
                }

                return (lines.ToArray(), charactersRead, false);
            }
        }

        return (lines.ToArray(), charactersRead, true);
    }

    private static async Task<IReadOnlyList<ConversationMemoryEntry>> LoadEntriesAsync(Stream stream, string path, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        var entries = new List<ConversationMemoryEntry>();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, 16 * 1024, leaveOpen: true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<ConversationMemoryEntry>(line, _jsonOptions)
                ?? throw new FormatException($"Conversation memory entry in `{path}` was empty.");
            ValidateEntry(entry, path);
            if (entries.Any(existing => string.Equals(existing.MessageId, entry.MessageId, StringComparison.Ordinal)
                || string.Equals(existing.PublicationId, entry.PublicationId, StringComparison.Ordinal)))
            {
                throw new FormatException($"Conversation memory entry in `{path}` reused a message or publication identity.");
            }

            entries.Add(entry);
        }

        return entries
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.TimestampUtc)
            .ToArray();
    }

    private async Task ArchiveCurrentConversationAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.ArchivedConversationMemoryPath);
        var archivePath = GetArchiveConversationPath();
        // Copy before replacing current so an interrupted rotation preserves the source transcript.
        // A duplicate archive is preferable to losing accepted conversation evidence.
        await CopyFileAsync(_paths.CurrentConversationPath, archivePath, overwrite: false, cancellationToken);
    }

    private string GetArchiveConversationPath()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff'Z'", CultureInfo.InvariantCulture);
        for (var suffix = 0; ; suffix++)
        {
            var conversationId = suffix == 0 ? timestamp : $"{timestamp}-{suffix}";
            var path = Path.Combine(_paths.ArchivedConversationMemoryPath, conversationId + ".ndjson");
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private string GetConversationPath(string conversationId)
    {
        var normalizedConversationId = NormalizeConversationId(conversationId);
        return TryGetArchivedConversationId(normalizedConversationId, out var archivedConversationId)
            ? Path.Combine(_paths.ArchivedConversationMemoryPath, archivedConversationId + ".ndjson")
            : Path.Combine(_paths.ConversationMemoryPath, normalizedConversationId + ".ndjson");
    }

    private IEnumerable<string> EnumerateConversationFiles()
    {
        foreach (var path in Directory.EnumerateFiles(_paths.ConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }

        if (!Directory.Exists(_paths.ArchivedConversationMemoryPath))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_paths.ArchivedConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly))
        {
            yield return path;
        }
    }

    private string GetConversationId(string path)
    {
        var conversationId = Path.GetFileNameWithoutExtension(path);
        return IsArchivedConversationPath(path)
            ? $"{ArchiveDirectoryName}/{conversationId}"
            : conversationId;
    }

    private bool IsArchivedConversationPath(string path)
    {
        var parentPath = Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var archivePath = Path.GetFullPath(_paths.ArchivedConversationMemoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(parentPath, archivePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var destinationMode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(destinationPath, destinationMode, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task WriteEntriesAsync(string path, IEnumerable<ConversationMemoryEntry> entries, CancellationToken cancellationToken)
    {
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, _jsonOptions)).ToArray();
        var text = lines.Length == 0 ? string.Empty : string.Join(Environment.NewLine, lines) + Environment.NewLine;
        await File.WriteAllTextAsync(path, text, cancellationToken);
    }

    private static string NormalizeConversationId(string conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var normalizedConversationId = conversationId.Trim().Replace('\\', '/');
        if (normalizedConversationId.EndsWith(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            normalizedConversationId = normalizedConversationId[..^".ndjson".Length];
        }

        var segments = normalizedConversationId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 1)
        {
            ValidateConversationFileName(segments[0], conversationId);
            return segments[0];
        }

        if (segments.Length == 2 && string.Equals(segments[0], ArchiveDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            ValidateConversationFileName(segments[1], conversationId);
            return $"{ArchiveDirectoryName}/{segments[1]}";
        }

        throw new ArgumentException("Conversation id must be a transcript file name or archive transcript path.", nameof(conversationId));
    }

    private static bool TryGetArchivedConversationId(string conversationId, out string archivedConversationId)
    {
        if (conversationId.StartsWith(ArchiveDirectoryName + "/", StringComparison.OrdinalIgnoreCase))
        {
            archivedConversationId = conversationId[(ArchiveDirectoryName.Length + 1)..];
            return true;
        }

        archivedConversationId = "";
        return false;
    }

    private static void ValidateConversationFileName(string fileName, string conversationId)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".." || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Conversation id must be a transcript file name or archive transcript path.", nameof(conversationId));
        }
    }

    private static LlmMessage ToMessage(ConversationMemoryEntry entry)
    {
        return new LlmMessage(ParseRole(entry.Role), entry.Content);
    }

    private static void ValidateEntry(ConversationMemoryEntry entry, string path)
    {
        if (entry.SchemaVersion != SchemaVersion)
        {
            throw new FormatException($"Unsupported conversation memory schema version `{entry.SchemaVersion}`.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ConversationId);
        if (string.IsNullOrWhiteSpace(entry.MessageId) || string.IsNullOrWhiteSpace(entry.PublicationId))
        {
            throw new ConversationTranscriptCleanupRequiredException(path);
        }

        _ = ParseRole(entry.Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Content);
    }

    private static bool IsCurrentConversationId(string conversationId)
    {
        return string.Equals(conversationId, CurrentConversationId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRole(ConversationMemoryEntry entry, LlmMessageRole role)
    {
        return ParseRole(entry.Role) == role;
    }

    private static LlmMessageRole ParseRole(string role)
    {
        if (!Enum.TryParse<LlmMessageRole>(role, ignoreCase: true, out var parsed) || parsed == LlmMessageRole.Unknown)
        {
            throw new FormatException($"Unsupported conversation memory role `{role}`.");
        }

        return parsed;
    }

    private sealed record CurrentConversationIdentity(int SchemaVersion, string ConversationId, string Version);
}
