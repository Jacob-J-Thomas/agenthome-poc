using System.Text.Json;
using EmbodySense.Core.Inference.Models;
using EmbodySense.Core.Memory.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Memory;

public sealed class ConversationMemoryStore
{
    private const int SchemaVersion = 1;
    private const string CurrentConversationId = "current";
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
        if (!File.Exists(_paths.CurrentConversationPath))
        {
            return [];
        }

        var entries = new List<ConversationMemoryEntry>();
        await foreach (var line in File.ReadLinesAsync(_paths.CurrentConversationPath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<ConversationMemoryEntry>(line, JsonOptions)
                ?? throw new FormatException($"Conversation memory entry in `{_paths.CurrentConversationPath}` was empty.");
            ValidateEntry(entry);
            entries.Add(entry);
        }

        return entries
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.TimestampUtc)
            .ToArray();
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

        if (!string.Equals(entry.ConversationId, CurrentConversationId, StringComparison.Ordinal))
        {
            throw new FormatException($"Unsupported conversation id `{entry.ConversationId}`.");
        }

        _ = ParseRole(entry.Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Content);
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
