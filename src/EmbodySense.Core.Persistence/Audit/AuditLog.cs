using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Governance.Audit.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Audit;

public sealed class AuditLog : IAuditLog
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
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
        var fileLock = FileLocks.GetOrAdd(_paths.EventsLogPath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var line = JsonSerializer.Serialize(auditEvent, JsonOptions);
            await File.AppendAllTextAsync(_paths.EventsLogPath, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
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

        var tailLines = new Queue<string>(limit);
        await using var stream = new FileStream(_paths.EventsLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (tailLines.Count == limit)
            {
                tailLines.Dequeue();
            }

            tailLines.Enqueue(line);
        }

        var events = new List<AuditEvent>();
        foreach (var line in tailLines)
        {
            try
            {
                var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, JsonOptions);
                if (auditEvent is not null)
                {
                    events.Add(auditEvent);
                }
            }
            catch (JsonException)
            {
            }
        }

        return events;
    }
}
