using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Inference.Models;
using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Memory;

public sealed class ConversationMemoryStore : IConversationMemoryStore
{
    private const int SchemaVersion = 1;
    private const int IdentitySchemaVersion = 1;
    private const string CurrentConversationId = "current";
    private const string ArchiveDirectoryName = "archive";
    private static readonly TimeSpan CurrentConversationLeaseRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CurrentConversationGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkspacePaths _paths;
    private readonly SemaphoreSlim _currentConversationGate;
    private string CurrentConversationIdentityPath => _paths.CurrentConversationPath + ".identity.json";
    private string CurrentConversationLockPath => _paths.CurrentConversationPath + ".lock";

    public ConversationMemoryStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _currentConversationGate = CurrentConversationGates.GetOrAdd(Path.GetFullPath(paths.CurrentConversationPath), _ => new SemaphoreSlim(1, 1));
    }

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

            await AppendMessageAsync(stream, message, currentEntries, cancellationToken);
            return true;
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
        await AppendMessageAsync(stream, message, entries, cancellationToken);
    }

    private FileStream OpenCurrentConversationForAtomicAppend()
    {
        return new FileStream(_paths.CurrentConversationPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 16 * 1024, FileOptions.Asynchronous);
    }

    private async Task<FileStream> AcquireCurrentConversationLeaseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(CurrentConversationLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(CurrentConversationLeaseRetryDelay, cancellationToken);
            }
        }
    }

    private static async Task AppendMessageAsync(FileStream stream, LlmMessage message, IReadOnlyList<ConversationMemoryEntry> entries, CancellationToken cancellationToken)
    {
        var sequence = entries.Count == 0 ? 1 : entries.Max(entry => entry.Sequence) + 1;
        var entry = new ConversationMemoryEntry(
            SchemaVersion,
            CurrentConversationId,
            sequence,
            DateTimeOffset.UtcNow,
            message.Role.ToString().ToLowerInvariant(),
            message.Content);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
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
        }
        catch
        {
            stream.SetLength(originalLength);
            stream.Position = originalLength;
            stream.Flush(flushToDisk: true);
            throw;
        }
    }

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
        var identity = JsonSerializer.Deserialize<CurrentConversationIdentity>(json, JsonOptions)
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
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(identity, JsonOptions), cancellationToken);
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

            var entry = JsonSerializer.Deserialize<ConversationMemoryEntry>(line, JsonOptions)
                ?? throw new FormatException($"Conversation memory entry in `{path}` was empty.");
            ValidateEntry(entry);
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

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        var destinationMode = overwrite ? FileMode.Create : FileMode.CreateNew;
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(destinationPath, destinationMode, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task WriteEntriesAsync(string path, IEnumerable<ConversationMemoryEntry> entries, CancellationToken cancellationToken)
    {
        var lines = entries.Select(entry => JsonSerializer.Serialize(entry, JsonOptions)).ToArray();
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

    private static void ValidateEntry(ConversationMemoryEntry entry)
    {
        if (entry.SchemaVersion != SchemaVersion)
        {
            throw new FormatException($"Unsupported conversation memory schema version `{entry.SchemaVersion}`.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ConversationId);
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
