using System.Text.Json;
using AgentHome.Core.Workspace;

namespace AgentHome.Core.Audit;

public sealed class AuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly WorkspacePaths _paths;

    public AuditLog(WorkspacePaths paths)
    {
        _paths = paths;
    }

    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.LogsPath);
        var json = JsonSerializer.Serialize(auditEvent, JsonOptions);
        await File.AppendAllTextAsync(_paths.EventsLogPath, json + Environment.NewLine, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ReadTailAsync(int count, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_paths.EventsLogPath))
        {
            return Array.Empty<string>();
        }

        var lines = await File.ReadAllLinesAsync(_paths.EventsLogPath, cancellationToken);
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .TakeLast(Math.Max(0, count))
            .ToArray();
    }
}
