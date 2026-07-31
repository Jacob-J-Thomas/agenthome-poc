using EmbodySense.Core.Common.Governance.Audit;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using EmbodySense.Core.Application.Governance.Audit;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Audit;

/// <summary>
/// Persists append-only audit events as newline-delimited JSON beneath the workspace audit directory.
/// </summary>
/// <remarks>
/// Appends targeting the same file are serialized across instances in this process. The store does not claim a cross-process
/// transaction. Tail reads preserve file order and ignore blank or malformed lines. A missing path, or one for which the
/// existence probe returns <see langword="false"/>, produces an empty result; cancellation and read failures after open propagate.
/// </remarks>
public sealed class AuditLog : IAuditLog
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly WorkspacePaths _paths;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuditLog"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    public AuditLog(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    /// <summary>
    /// Creates an audit sink only when the requested root already contains EmbodySense agent scaffolding.
    /// </summary>
    /// <param name="rootPath">The absolute workspace root path.</param>
    /// <returns>The workspace audit sink, or <see langword="null"/> for a missing, blank, or uninitialized root.</returns>
    public static AuditLog? TryCreateForExistingWorkspace(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var paths = new WorkspacePaths(rootPath);

        return Directory.Exists(paths.AgentPath) ? new AuditLog(paths) : null;
    }

    /// <summary>
    /// Appends one canonical audit event as a single newline-delimited JSON record.
    /// </summary>
    /// <param name="auditEvent">The audit event.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes after the record has been appended to the workspace audit file.</returns>
    public async Task AppendAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        Directory.CreateDirectory(_paths.AuditPath);
        var fileLock = _fileLocks.GetOrAdd(_paths.EventsLogPath, _ => new SemaphoreSlim(1, 1));

        await fileLock.WaitAsync(cancellationToken);
        try
        {
            var line = JsonSerializer.Serialize(auditEvent, _jsonOptions);
            await File.AppendAllTextAsync(_paths.EventsLogPath, line + Environment.NewLine, cancellationToken);
        }
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    /// Reads the last bounded set of nonblank audit lines and returns the records that deserialize successfully in file order.
    /// </summary>
    /// <param name="limit">The limit.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>
    /// Up to <paramref name="limit"/> deserialized events from the last <paramref name="limit"/> nonblank lines. Malformed
    /// lines are skipped without backfilling from older lines, and a missing audit file produces an empty result.
    /// </returns>
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

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                var auditEvent = JsonSerializer.Deserialize<AuditEvent>(line, _jsonOptions);
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
