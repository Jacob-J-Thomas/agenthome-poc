using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Audit;
using EmbodySense.Core.Startup.Configuration;
using EmbodySense.Core.Startup.Workspace;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Startup.Tests.Configuration;

public sealed class WorkspaceConfigurationReaderTests
{
    [Fact]
    public async Task ReadAsync_returns_initialized_workspace_configuration_details()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await WriteTranscriptAsync(paths.CurrentConversationPath, "current", "current prompt", "current answer");
        Directory.CreateDirectory(paths.ArchivedConversationMemoryPath);
        await WriteTranscriptAsync(Path.Combine(paths.ArchivedConversationMemoryPath, "20260601.ndjson"), "20260601", "archived prompt", "archived answer");
        await new AuditLog(paths).AppendAsync(AuditEvent.Create(
            "test.actor",
            "test.action",
            "target",
            "ok",
            "detail",
            new Dictionary<string, object?> { ["count"] = 2 }));

        var snapshot = await new WorkspaceConfigurationReader().ReadAsync(workspace.RootPath, Runtime());

        Assert.True(snapshot.Status.Initialized);
        Assert.Equal("http://127.0.0.1:4378", snapshot.Runtime.Url);
        Assert.Contains(snapshot.Paths, item => item.Name == "Audit log" && item.Exists);
        Assert.True(snapshot.Permissions.Exists);
        Assert.True(snapshot.Permissions.Parsed);
        Assert.NotEmpty(snapshot.Permissions.Approved);
        Assert.NotEmpty(snapshot.Permissions.Denied);
        Assert.Contains("\"version\": 2", snapshot.Permissions.RawJson);
        Assert.Contains(snapshot.Documents, document => document.Name == "Role guide" && document.Category == "Role" && document.Exists && document.Content.Contains("Workspace role guide", StringComparison.Ordinal));
        Assert.Contains(snapshot.Documents, document => document.Name == "Soul" && document.Exists);
        Assert.Contains(snapshot.Documents, document => document.Name == "Personality" && document.Exists);
        Assert.Contains(snapshot.Audit.Events, auditEvent => auditEvent.Actor == "test.actor" && auditEvent.Metadata["count"] == "2");
        Assert.Contains(snapshot.ConversationHistory.Transcripts, transcript => transcript.ConversationId == "current" && transcript.MessageCount == 2 && transcript.FirstPrompt == "current prompt");
        Assert.Contains(snapshot.ConversationHistory.Transcripts, transcript => transcript.ConversationId == "archive/20260601" && transcript.Messages.Any(message => message.Content == "archived answer"));
        Assert.Contains(snapshot.Concepts, concept => concept.Name == "Conversation history" && concept.Status == "Present");
    }

    [Fact]
    public async Task ReadAsync_reports_missing_workspace_configuration_without_throwing()
    {
        using var workspace = new TestWorkspace();

        var snapshot = await new WorkspaceConfigurationReader().ReadAsync(workspace.RootPath, Runtime());

        Assert.False(snapshot.Status.Initialized);
        Assert.False(snapshot.Permissions.Exists);
        Assert.Contains("permissions.json is missing", snapshot.Permissions.ReadProblems.Single());
        Assert.Contains(snapshot.Documents, document => document.Name == "Role guide" && !document.Exists);
        var current = Assert.Single(snapshot.ConversationHistory.Transcripts);
        Assert.Equal("current", current.ConversationId);
        Assert.False(current.Exists);
        Assert.Contains(snapshot.Concepts, concept => concept.Name == "Audit" && concept.Status == "Missing");
    }

    [Fact]
    public async Task ReadAsync_surfaces_read_problems_for_invalid_policy_audit_and_transcript_lines()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        Directory.CreateDirectory(paths.AgentPath);
        Directory.CreateDirectory(paths.AuditPath);
        Directory.CreateDirectory(paths.ConversationMemoryPath);
        await File.WriteAllTextAsync(paths.PermissionsPath, "{ nope");
        await File.WriteAllTextAsync(paths.EventsLogPath, "{ nope" + Environment.NewLine);
        await File.WriteAllTextAsync(paths.CurrentConversationPath, "{ nope" + Environment.NewLine);

        var snapshot = await new WorkspaceConfigurationReader().ReadAsync(workspace.RootPath, Runtime());

        Assert.True(snapshot.Permissions.Exists);
        Assert.False(snapshot.Permissions.Parsed);
        Assert.NotEmpty(snapshot.Permissions.ReadProblems);
        Assert.NotEmpty(snapshot.Audit.ReadProblems);
        Assert.NotEmpty(snapshot.ConversationHistory.ReadProblems);
    }

    [Fact]
    public async Task ReadAsync_caps_sensitive_snapshot_content()
    {
        using var workspace = new TestWorkspace();
        await new WorkspaceInitializer().InitializeAsync(workspace.RootPath);
        var paths = new WorkspacePaths(workspace.RootPath);
        await File.WriteAllTextAsync(paths.PermissionsPath, """{"version":2,"scope":"test","access_token":"secret-value","approved":[],"denied":[]}""");
        await File.WriteAllTextAsync(paths.AgentFile("ROLE.md"), "api_key=secret-value" + Environment.NewLine + new string('a', 41_000));
        await WriteLongTranscriptAsync(paths.CurrentConversationPath);
        var auditLog = new AuditLog(paths);
        for (var i = 0; i < 205; i++)
        {
            await auditLog.AppendAsync(AuditEvent.Create("test", "event." + i, "target", "ok", "detail"));
        }

        var snapshot = await new WorkspaceConfigurationReader().ReadAsync(workspace.RootPath, Runtime());

        Assert.Contains("access_token\": [redacted]", snapshot.Permissions.RawJson);
        var roleGuide = Assert.Single(snapshot.Documents, document => document.Name == "Role guide");
        Assert.Contains("api_key= [redacted]", roleGuide.Content);
        Assert.Contains("[truncated after", roleGuide.Content);
        Assert.Equal(200, snapshot.Audit.Events.Count);
        Assert.Contains(snapshot.Audit.ReadProblems, problem => problem.Contains("omits", StringComparison.Ordinal) && problem.Contains("older events", StringComparison.Ordinal));
        var current = Assert.Single(snapshot.ConversationHistory.Transcripts, transcript => transcript.ConversationId == "current");
        Assert.Equal(205, current.MessageCount);
        Assert.Equal(200, current.Messages.Count);
        Assert.Contains(snapshot.ConversationHistory.ReadProblems, problem => problem.Contains("omits 5 later messages", StringComparison.Ordinal));
    }

    private static WorkspaceRuntimeConfiguration Runtime()
    {
        return new WorkspaceRuntimeConfiguration(
            "web",
            "http://127.0.0.1:4378",
            "configured externally",
            "codex from PATH",
            "read-only",
            "Localhost web client.");
    }

    private static async Task WriteTranscriptAsync(string path, string conversationId, string prompt, string answer)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""
            {"schemaVersion":1,"conversationId":"{{conversationId}}","sequence":1,"timestampUtc":"2026-06-01T00:01:00+00:00","role":"user","content":"{{prompt}}"}
            {"schemaVersion":1,"conversationId":"{{conversationId}}","sequence":2,"timestampUtc":"2026-06-01T00:02:00+00:00","role":"assistant","content":"{{answer}}"}
            """);
    }

    private static async Task WriteLongTranscriptAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = Enumerable.Range(1, 205).Select(index =>
        {
            var role = index % 2 == 0 ? "assistant" : "user";
            return $$"""{"schemaVersion":1,"conversationId":"current","sequence":{{index}},"timestampUtc":"2026-06-01T00:01:00+00:00","role":"{{role}}","content":"message {{index}}"}""";
        });
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }
}
