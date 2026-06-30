using System.Text.Json;
using System.Text;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Memory.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Startup.Workspace;

namespace EmbodySense.Core.Startup.Configuration;

public sealed class WorkspaceConfigurationReader
{
    // TODO: revisit what appropriate figures should actually be.
    private const int MaxRawJsonCharacters = 40_000;
    private const int MaxDocumentCharacters = 40_000;
    private const int MaxAuditEvents = 200;
    private const int MaxReadProblems = 100;
    private const int MaxConversationFiles = 50;
    private const int MaxConversationMessagesPerTranscript = 200;
    private const int MaxConversationMessageCharacters = 4_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public async Task<WorkspaceConfigurationSnapshot> ReadAsync(string rootPath, WorkspaceRuntimeConfiguration runtime, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(runtime);

        var paths = new WorkspacePaths(rootPath);
        var status = new WorkspaceStatusReader().Read(paths.RootPath);
        var permissions = await ReadPermissionsAsync(paths, status.DefaultAccess, cancellationToken);
        var documents = await ReadDocumentsAsync(paths, cancellationToken);
        var audit = await ReadAuditAsync(paths, cancellationToken);
        var conversationHistory = await ReadConversationHistoryAsync(paths, cancellationToken);

        return new WorkspaceConfigurationSnapshot(
            DateTimeOffset.UtcNow,
            runtime,
            new WorkspaceConfigurationStatus(status.RootPath, status.IsInitialized, status.DefaultAccess),
            GetPaths(paths),
            permissions,
            documents,
            audit,
            conversationHistory,
            GetConcepts(paths));
    }

    private static IReadOnlyList<WorkspaceConfigurationPath> GetPaths(WorkspacePaths paths)
    {
        return
        [
            PathItem("Root", "Workspace", paths.RootPath, "Selected harness workspace root."),
            PathItem("Agent home", "Agent", paths.AgentPath, "Durable agent environment and configuration root."),
            PathItem("Working area", "Workspace", paths.WorkspacePath, "Same as the selected root; contains human/agent boundary folders."),
            PathItem("Private workspace", "Workspace", paths.WorkspacePrivatePath, "Human-private workspace area denied by default."),
            PathItem("Shared workspace", "Workspace", paths.WorkspaceSharedPath, "Human and agent shared workspace area."),
            PathItem("Generated workspace", "Workspace", paths.WorkspaceGeneratedPath, "Agent-generated workspace output area."),
            PathItem("System workspace", "Workspace", paths.WorkspaceSystemPath, "Read-only system workspace area."),
            PathItem("Permissions", "Governance", paths.PermissionsPath, "Directory permission policy used by governed tools."),
            PathItem("Audit log", "Governance", paths.EventsLogPath, "Append-only audit event stream."),
            PathItem("Memory", "Agent", paths.AgentFile("MEMORY.md"), "Primary durable memory registry."),
            PathItem("Conversation history", "Memory", paths.ConversationMemoryPath, "Searchable transcript evidence."),
            PathItem("Archived conversations", "Memory", paths.ArchivedConversationMemoryPath, "Archived transcript evidence."),
            PathItem("Tasks", "Agent", paths.TasksPath, "Durable task state path."),
            PathItem("Exports", "Agent", paths.ExportsPath, "Governed export path."),
            PathItem("Skills", "Agent", paths.SkillsPath, "Agent skill path."),
            PathItem("Hooks", "Agent", paths.HooksPath, "Hook configuration path."),
            PathItem("Recipes", "Agent", paths.RecipesPath, "Agent recipe path."),
            PathItem("Logs", "Governance", paths.LogsPath, "Harness log path denied to agent tools by default.")
        ];
    }

    private static WorkspaceConfigurationPath PathItem(string name, string category, string path, string description)
    {
        return new WorkspaceConfigurationPath(name, category, path, Directory.Exists(path) || File.Exists(path), description);
    }

    private static async Task<WorkspacePermissionsConfiguration> ReadPermissionsAsync(WorkspacePaths paths, string defaultAccess, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.PermissionsPath))
        {
            return new WorkspacePermissionsConfiguration(
                paths.PermissionsPath,
                false,
                false,
                null,
                "",
                defaultAccess,
                "",
                [],
                [],
                ["permissions.json is missing."]);
        }

        var problems = new List<string>();
        var (rawJson, rawJsonTruncated) = await ReadCappedTextAsync(paths.PermissionsPath, MaxRawJsonCharacters, cancellationToken);
        var rawJsonForParsing = rawJson;
        var displayRawJson = RedactLikelySecrets(rawJson);
        if (rawJsonTruncated)
        {
            AddProblem(problems, $"permissions.json was truncated after {MaxRawJsonCharacters} characters.");
            displayRawJson += Environment.NewLine + $"[truncated after {MaxRawJsonCharacters} characters]";
        }

        PermissionsDocument? document = null;
        try
        {
            document = PermissionsDocument.FromJson(rawJsonForParsing);
            if (document is null)
            {
                problems.Add("permissions.json is unsupported or does not declare version 2.");
            }
        }
        catch (JsonException exception)
        {
            problems.Add(exception.Message);
        }

        return new WorkspacePermissionsConfiguration(
            paths.PermissionsPath,
            true,
            document is not null,
            document?.Version,
            document?.Scope ?? "",
            defaultAccess,
            displayRawJson,
            FormatApprovedRules(document?.Approved ?? []),
            FormatDeniedRules(document?.Denied ?? []),
            problems);
    }

    private static IReadOnlyList<WorkspacePermissionRule> FormatApprovedRules(IReadOnlyList<ApprovedFileSystemPermission> entries)
    {
        return entries
            .Select(entry => new WorkspacePermissionRule(
                "Approved",
                entry.Path,
                FormatOperations(entry.Operations),
                entry.RequiresApproval,
                entry.RequiresApproval ? "Human approval is required before use." : "Allowed without additional human approval."))
            .ToArray();
    }

    private static IReadOnlyList<WorkspacePermissionRule> FormatDeniedRules(IReadOnlyList<DeniedFileSystemPermission> entries)
    {
        return entries
            .Select(entry => new WorkspacePermissionRule(
                "Denied",
                entry.Path,
                FormatOperations(entry.Operations),
                true,
                "Denied unless the human changes the policy."))
            .ToArray();
    }

    private static IReadOnlyList<string> FormatOperations(IReadOnlyList<FileSystemOperation> operations)
    {
        return operations.Select(operation => operation.ToString().ToLowerInvariant()).ToArray();
    }

    private static async Task<IReadOnlyList<WorkspaceConfigurationDocument>> ReadDocumentsAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        var documentPaths = new[]
        {
            DocumentPath("Nearest AGENTS", "Repository", WorkspaceInstructionLocator.FindNearest(paths.RootPath) ?? Path.Combine(paths.RootPath, WorkspaceInstructionLocator.FileName)),
            DocumentPath("Agent guide", "Agent", paths.AgentFile("AGENT.md")),
            DocumentPath("Soul", "Agent", paths.AgentFile("SOUL.md")),
            DocumentPath("Personality", "Agent", paths.AgentFile("PERSONALITY.md")),
            DocumentPath("Context", "Agent", paths.AgentFile("CONTEXT.md")),
            DocumentPath("Memory", "Agent", paths.AgentFile("MEMORY.md")),
            DocumentPath("Models", "Agent", paths.AgentFile("models.json")),
            DocumentPath("Permissions guide", "Governance", paths.PermissionsReadmePath),
            DocumentPath("Memory guide", "Memory", paths.MemoryReadmePath),
            DocumentPath("Audit guide", "Governance", paths.AuditReadmePath)
        };

        var documents = new List<WorkspaceConfigurationDocument>();
        foreach (var item in documentPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.Add(await ReadDocumentAsync(item.Name, item.Category, item.Path, cancellationToken));
        }

        return documents;
    }

    private static (string Name, string Category, string Path) DocumentPath(string name, string category, string path)
    {
        return (name, category, path);
    }

    private static async Task<WorkspaceConfigurationDocument> ReadDocumentAsync(string name, string category, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new WorkspaceConfigurationDocument(
                name,
                category,
                path,
                false,
                0,
                null,
                "");
        }

        var fileInfo = new FileInfo(path);
        try
        {
            var (content, truncated) = await ReadCappedTextAsync(path, MaxDocumentCharacters, cancellationToken);
            content = RedactLikelySecrets(content);
            if (truncated)
            {
                content += Environment.NewLine + $"[truncated after {MaxDocumentCharacters} characters]";
            }

            return new WorkspaceConfigurationDocument(
                name,
                category,
                path,
                true,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                content);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceConfigurationDocument(
                name,
                category,
                path,
                true,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc,
                $"Unable to read file: {exception.Message}");
        }
    }

    private static async Task<WorkspaceAuditConfiguration> ReadAuditAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.EventsLogPath))
        {
            return new WorkspaceAuditConfiguration(paths.EventsLogPath, false, [], []);
        }

        var events = new List<WorkspaceAuditLogEvent>();
        var problems = new List<string>();
        var lineNumber = 0;
        var omittedEvents = 0;
        await foreach (var line in File.ReadLinesAsync(paths.EventsLogPath, cancellationToken))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, JsonOptions);
                if (auditEvent is null)
                {
                    AddProblem(problems, $"Audit line {lineNumber} was empty after parsing.");
                    continue;
                }

                if (events.Count == MaxAuditEvents)
                {
                    events.RemoveAt(0);
                    omittedEvents++;
                }

                events.Add(new WorkspaceAuditLogEvent(
                    lineNumber,
                    auditEvent.TimestampUtc,
                    auditEvent.Actor,
                    auditEvent.Action,
                    auditEvent.Target,
                    auditEvent.Outcome,
                    auditEvent.Detail,
                    FormatMetadata(auditEvent.Metadata)));
            }
            catch (JsonException exception)
            {
                AddProblem(problems, $"Audit line {lineNumber}: {exception.Message}");
            }
        }

        if (omittedEvents > 0)
        {
            AddProblem(problems, $"Audit snapshot includes the latest {MaxAuditEvents} events and omits {omittedEvents} older events.");
        }

        return new WorkspaceAuditConfiguration(paths.EventsLogPath, true, events, problems);
    }

    private static IReadOnlyDictionary<string, string> FormatMetadata(IReadOnlyDictionary<string, object?> metadata)
    {
        return metadata.ToDictionary(item => item.Key, item => FormatMetadataValue(item.Value), StringComparer.Ordinal);
    }

    private static string FormatMetadataValue(object? value)
    {
        return value switch
        {
            null => "",
            JsonElement element => FormatJsonElement(element),
            _ => value.ToString() ?? ""
        };
    }

    private static string FormatJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => element.GetRawText()
        };
    }

    private static async Task<WorkspaceConversationHistoryConfiguration> ReadConversationHistoryAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        var transcripts = new List<WorkspaceConversationTranscript>();
        var problems = new List<string>();

        transcripts.Add(await ReadTranscriptAsync(paths.CurrentConversationPath, "current", true, cancellationToken, problems));
        if (Directory.Exists(paths.ConversationMemoryPath))
        {
            foreach (var path in Directory.EnumerateFiles(paths.ConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly).Where(path => !SamePath(path, paths.CurrentConversationPath)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (transcripts.Count >= MaxConversationFiles)
                {
                    AddProblem(problems, $"Conversation snapshot includes {MaxConversationFiles} transcript files and omits additional files.");
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                transcripts.Add(await ReadTranscriptAsync(path, Path.GetFileNameWithoutExtension(path), false, cancellationToken, problems));
            }
        }

        if (Directory.Exists(paths.ArchivedConversationMemoryPath))
        {
            foreach (var path in Directory.EnumerateFiles(paths.ArchivedConversationMemoryPath, "*.ndjson", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (transcripts.Count >= MaxConversationFiles)
                {
                    AddProblem(problems, $"Conversation snapshot includes {MaxConversationFiles} transcript files and omits additional files.");
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                transcripts.Add(await ReadTranscriptAsync(path, "archive/" + Path.GetFileNameWithoutExtension(path), false, cancellationToken, problems));
            }
        }

        return new WorkspaceConversationHistoryConfiguration(
            paths.ConversationMemoryPath,
            paths.CurrentConversationPath,
            paths.ArchivedConversationMemoryPath,
            Directory.Exists(paths.ConversationMemoryPath),
            transcripts,
            problems);
    }

    private static async Task<WorkspaceConversationTranscript> ReadTranscriptAsync(string path, string conversationId, bool isCurrent, CancellationToken cancellationToken, List<string> problems)
    {
        if (!File.Exists(path))
        {
            return new WorkspaceConversationTranscript(
                conversationId,
                path,
                false,
                isCurrent,
                0,
                null,
                null,
                "",
                []);
        }

        var messages = new List<WorkspaceConversationMessage>();
        var lineNumber = 0;
        var parsedMessageCount = 0;
        var omittedMessages = 0;
        await foreach (var line in File.ReadLinesAsync(path, cancellationToken))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<ConversationMemoryEntry>(line, JsonOptions);
                if (entry is null)
                {
                    AddProblem(problems, $"{conversationId} line {lineNumber} was empty after parsing.");
                    continue;
                }

                parsedMessageCount++;
                if (messages.Count < MaxConversationMessagesPerTranscript)
                {
                    messages.Add(new WorkspaceConversationMessage(
                        entry.Sequence,
                        entry.TimestampUtc,
                        entry.Role,
                        Truncate(entry.Content, MaxConversationMessageCharacters)));
                }
                else
                {
                    omittedMessages++;
                }
            }
            catch (JsonException exception)
            {
                AddProblem(problems, $"{conversationId} line {lineNumber}: {exception.Message}");
            }
        }

        if (omittedMessages > 0)
        {
            AddProblem(problems, $"{conversationId} snapshot includes the first {MaxConversationMessagesPerTranscript} messages and omits {omittedMessages} later messages.");
        }

        var orderedMessages = messages.OrderBy(message => message.Sequence).ThenBy(message => message.TimestampUtc).ToArray();
        var firstPrompt = orderedMessages.FirstOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        var firstTimestamp = orderedMessages.Length == 0 ? (DateTimeOffset?)null : orderedMessages[0].TimestampUtc;
        var lastTimestamp = orderedMessages.Length == 0 ? (DateTimeOffset?)null : orderedMessages[^1].TimestampUtc;
        return new WorkspaceConversationTranscript(
            conversationId,
            path,
            true,
            isCurrent,
            parsedMessageCount,
            firstTimestamp,
            lastTimestamp,
            firstPrompt,
            orderedMessages);
    }

    private static bool SamePath(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedTextAsync(string path, int maxCharacters, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxCharacters + 1];
        var count = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
        var take = Math.Min(count, maxCharacters);
        return (new string(buffer, 0, take), count > maxCharacters || stream.Position < stream.Length);
    }

    private static string RedactLikelySecrets(string text)
    {
        var markers = new[] { "api_key", "apikey", "access_token", "auth_token", "password", "client_secret", "secret_key" };
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var lower = lines[i].ToLowerInvariant();
            var marker = markers.FirstOrDefault(lower.Contains);
            if (marker is null)
            {
                continue;
            }

            var markerIndex = lower.IndexOf(marker, StringComparison.Ordinal);
            var separatorIndex = lines[i].IndexOfAny([':', '='], markerIndex + marker.Length);
            lines[i] = separatorIndex >= 0
                ? lines[i][..(separatorIndex + 1)] + " [redacted]"
                : "[redacted likely secret-bearing line]";
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Truncate(string value, int maxCharacters)
    {
        return value.Length <= maxCharacters
            ? value
            : value[..maxCharacters] + Environment.NewLine + $"[truncated after {maxCharacters} characters]";
    }

    private static void AddProblem(List<string> problems, string problem)
    {
        if (problems.Count < MaxReadProblems)
        {
            problems.Add(problem);
        }
        else if (problems.Count == MaxReadProblems)
        {
            problems.Add("Additional read problems omitted.");
        }
    }

    private static IReadOnlyList<WorkspaceConfigurationConcept> GetConcepts(WorkspacePaths paths)
    {
        return
        [
            Concept("Permissions", "Governance", File.Exists(paths.PermissionsPath), "Directory-level policy; missing and unmatched rules require approval."),
            Concept("Audit", "Governance", File.Exists(paths.EventsLogPath), "Append-only event stream for governed harness behavior."),
            Concept("Memory", "Agent", File.Exists(paths.AgentFile("MEMORY.md")), "Primary durable memory registry."),
            Concept("Conversation history", "Memory", Directory.Exists(paths.ConversationMemoryPath), "Searchable transcript evidence separate from durable memory."),
            Concept("Models", "Agent", File.Exists(paths.AgentFile("models.json")), "Role-to-provider model configuration placeholder."),
            Concept("Skills", "Agent", Directory.Exists(paths.SkillsPath), "Agent extension instructions and generated skills path."),
            Concept("Hooks", "Agent", Directory.Exists(paths.HooksPath), "Hook configuration path; denied by default until policy changes."),
            Concept("Recipes", "Agent", Directory.Exists(paths.RecipesPath), "Recipe configuration path for repeatable agent work."),
            Concept("Tasks", "Agent", Directory.Exists(paths.TasksPath), "Durable task-state path for current and future planning surfaces.")
        ];
    }

    private static WorkspaceConfigurationConcept Concept(string name, string category, bool exists, string detail)
    {
        return new WorkspaceConfigurationConcept(
            name,
            category,
            exists ? "Present" : "Missing",
            detail);
    }
}
