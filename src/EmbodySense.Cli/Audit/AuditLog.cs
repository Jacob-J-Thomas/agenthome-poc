using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Cli.Workspace;

namespace EmbodySense.Cli.Audit;

internal sealed class AuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
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
}
