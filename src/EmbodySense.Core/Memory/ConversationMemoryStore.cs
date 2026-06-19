using System.Globalization;
using System.Text.Json;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Memory;

public sealed class ConversationMemoryStore : IConversationMemoryStore
{
    private const int SchemaVersion = 1;
    private const string CurrentConversationId = "current";
    private const string ArchiveDirectoryName = "archive";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WorkspacePaths _paths;

    public ConversationMemoryStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    public async Task<IReadOnlyList<LlmMessage>> LoadCurrentConversationAsync(CancellationToken cancellationToken = default)
    {
        var entries = await LoadCurrentEntriesAsync(cancellationToken);
        return entries.Select(ToMessage).ToArray();
    }

    public async Task<IReadOnlyList<ConversationTranscriptListItem>> ListConversationsAsync(CancellationToken cancellationToken = default)
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
        Directory.CreateDirectory(_paths.ConversationMemoryPath);
        if (File.Exists(_paths.CurrentConversationPath) && new FileInfo(_paths.CurrentConversationPath).Length > 0)
        {
            await ArchiveCurrentConversationAsync(cancellationToken);
        }

        await File.WriteAllTextAsync(_paths.CurrentConversationPath, string.Empty, cancellationToken);
    }

    public async Task<IReadOnlyList<LlmMessage>> LoadConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var path = GetConversationPath(conversationId);
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

        var sourcePath = GetConversationPath(normalizedConversationId);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"Conversation `{conversationId}` was not found.", sourcePath);
        }

        var sourceEntries = await LoadEntriesAsync(sourcePath, cancellationToken);
        Directory.CreateDirectory(_paths.ConversationMemoryPath);
        if (File.Exists(_paths.CurrentConversationPath) && new FileInfo(_paths.CurrentConversationPath).Length > 0)
        {
            await ArchiveCurrentConversationAsync(cancellationToken);
        }

        await WriteEntriesAsync(_paths.CurrentConversationPath, sourceEntries.Select(entry => entry with { ConversationId = CurrentConversationId }), cancellationToken);
    }

    public async Task AppendMessageAsync(LlmMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        Directory.CreateDirectory(_paths.ConversationMemoryPath);
        var sequence = await GetNextSequenceAsync(cancellationToken);
        var entry = new ConversationMemoryEntry(
            SchemaVersion,
            CurrentConversationId,
            sequence,
            DateTimeOffset.UtcNow,
            message.Role.ToString().ToLowerInvariant(),
            message.Content);
        var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
        await File.AppendAllTextAsync(_paths.CurrentConversationPath, line, cancellationToken);
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

    private async Task<int> GetNextSequenceAsync(CancellationToken cancellationToken)
    {
        var entries = await LoadCurrentEntriesAsync(cancellationToken);
        return entries.Count == 0 ? 1 : entries.Max(entry => entry.Sequence) + 1;
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

        var entries = new List<ConversationMemoryEntry>();
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
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
}
