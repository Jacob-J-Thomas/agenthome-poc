using System.Text.Json;
using AgentHome.Core.Audit;
using AgentHome.Core.Workspace;

namespace AgentHome.Core.Tasks;

public sealed class TaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly WorkspacePaths _paths;
    private readonly AuditLog _auditLog;

    public TaskStore(WorkspacePaths paths)
    {
        _paths = paths;
        _auditLog = new AuditLog(paths);
    }

    public async Task<AgentTask> StartAsync(string goal, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(goal))
        {
            throw new ArgumentException("Task goal is required.", nameof(goal));
        }

        Directory.CreateDirectory(_paths.TasksPath);

        var id = CreateTaskId(goal);
        var task = new AgentTask
        {
            Id = id,
            Goal = goal.Trim(),
            Status = "active",
            CreatedAt = DateTimeOffset.UtcNow
        };

        var taskDirectory = Path.Combine(_paths.TasksPath, id);
        Directory.CreateDirectory(taskDirectory);

        var taskPath = Path.Combine(taskDirectory, "task.json");
        var json = JsonSerializer.Serialize(task, JsonOptions);
        await File.WriteAllTextAsync(taskPath, json + Environment.NewLine, cancellationToken);

        await _auditLog.AppendAsync(new AuditEvent(
            Actor: "agenthome.cli",
            Action: "task.start",
            Target: taskPath,
            Decision: "Allow",
            TaskId: task.Id,
            Detail: task.Goal), cancellationToken);

        return task;
    }

    private static string CreateTaskId(string goal)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        var slug = new string(goal
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray());

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Trim('-');
        if (slug.Length > 48)
        {
            slug = slug[..48].Trim('-');
        }

        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "task";
        }

        return $"{timestamp}-{slug}";
    }
}
