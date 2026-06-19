using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Audit.Models;
using EmbodySense.Core.Workspace.Models;

namespace EmbodySense.Core.Audit;

public sealed class AuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly WorkspacePaths _paths;

    public AuditLog(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    public static AuditLog? TryCreateForExistingWorkspace(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var paths = new WorkspacePaths(rootPath);

        return Directory.Exists(paths.AgentPath) ? new AuditLog(paths) : null;
    }

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        Directory.CreateDirectory(_paths.AuditPath);

        var line = JsonSerializer.Serialize(auditEvent, JsonOptions);
        await File.AppendAllTextAsync(
            _paths.EventsLogPath,
            line + Environment.NewLine,
            cancellationToken);
    }

    public async Task<IReadOnlyList<AuditEvent>> ReadTailAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be greater than zero.");
        }

        if (!File.Exists(_paths.EventsLogPath))
        {
            return [];
        }

        var lines = await File.ReadAllLinesAsync(_paths.EventsLogPath, cancellationToken);
        var events = new List<AuditEvent>();

        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)).TakeLast(limit))
        {
            var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, JsonOptions);

            if (auditEvent is not null)
            {
                events.Add(auditEvent);
            }
        }

        return events;
    }
}
