using System.Text;
using AgentHome.Core.Audit;
using AgentHome.Core.Workspace;

namespace AgentHome.Core.Context;

public sealed class ContextExporter
{
    private readonly WorkspacePaths _paths;
    private readonly AuditLog _auditLog;

    public ContextExporter(WorkspacePaths paths)
    {
        _paths = paths;
        _auditLog = new AuditLog(paths);
    }

    public async Task<string> ExportCodexAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.ExportsPath);

        var outputPath = Path.Combine(_paths.ExportsPath, "codex-context.md");
        var builder = new StringBuilder();

        builder.AppendLine("# Codex context export");
        builder.AppendLine();
        builder.AppendLine("This file was generated from the local AgentHome environment.");
        builder.AppendLine("Codex should treat this as context, not as a replacement for `.agent/` state.");
        builder.AppendLine();

        await AppendFileSectionAsync(builder, "Agent operating guide", _paths.AgentFile("AGENT.md"), cancellationToken);
        await AppendFileSectionAsync(builder, "Workspace context", _paths.AgentFile("CONTEXT.md"), cancellationToken);
        await AppendFileSectionAsync(builder, "Memory", _paths.AgentFile("MEMORY.md"), cancellationToken);
        await AppendFileSectionAsync(builder, "Permissions", _paths.AgentFile("permissions.json"), cancellationToken, fencedLanguage: "json");
        await AppendFileSectionAsync(builder, "Models", _paths.AgentFile("models.json"), cancellationToken, fencedLanguage: "json");
        await AppendLatestTasksAsync(builder, cancellationToken);

        await File.WriteAllTextAsync(outputPath, builder.ToString(), cancellationToken);

        await _auditLog.AppendAsync(new AuditEvent(
            Actor: "agenthome.cli",
            Action: "context.export",
            Target: outputPath,
            Decision: "Allow",
            TaskId: null,
            Detail: "Exported Codex context."), cancellationToken);

        return outputPath;
    }

    private static async Task AppendFileSectionAsync(
        StringBuilder builder,
        string title,
        string path,
        CancellationToken cancellationToken,
        string? fencedLanguage = null)
    {
        builder.AppendLine($"## {title}");
        builder.AppendLine();

        if (!File.Exists(path))
        {
            builder.AppendLine("_File missing._");
            builder.AppendLine();
            return;
        }

        var content = await File.ReadAllTextAsync(path, cancellationToken);
        if (!string.IsNullOrWhiteSpace(fencedLanguage))
        {
            builder.AppendLine($"```{fencedLanguage}");
            builder.AppendLine(content.TrimEnd());
            builder.AppendLine("```");
        }
        else
        {
            builder.AppendLine(content.TrimEnd());
        }

        builder.AppendLine();
    }

    private async Task AppendLatestTasksAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        builder.AppendLine("## Latest tasks");
        builder.AppendLine();

        if (!Directory.Exists(_paths.TasksPath))
        {
            builder.AppendLine("_No tasks directory._");
            builder.AppendLine();
            return;
        }

        var taskFiles = Directory.GetFiles(_paths.TasksPath, "task.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(5)
            .ToArray();

        if (taskFiles.Length == 0)
        {
            builder.AppendLine("_No tasks yet._");
            builder.AppendLine();
            return;
        }

        foreach (var taskFile in taskFiles)
        {
            var relativePath = Path.GetRelativePath(_paths.RootPath, taskFile).Replace('\\', '/');
            var content = await File.ReadAllTextAsync(taskFile, cancellationToken);
            builder.AppendLine($"### {relativePath}");
            builder.AppendLine();
            builder.AppendLine("```json");
            builder.AppendLine(content.TrimEnd());
            builder.AppendLine("```");
            builder.AppendLine();
        }
    }
}
