using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Runtime.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class LoopRunStore : ILoopRunStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) } };
    private readonly WorkspacePaths _paths;

    public LoopRunStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    public async Task SaveAsync(LoopRunRecord run, CancellationToken cancellationToken = default)
    {
        ValidateRun(run);

        var path = LoopArtifactPaths.GetRunPath(_paths, run.LoopId, run.RunId);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _paths.LoopRunsPath);
        var json = JsonSerializer.Serialize(run, JsonOptions) + Environment.NewLine;
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task<LoopRunRecord?> LoadAsync(string loopId, string runId, CancellationToken cancellationToken = default)
    {
        var path = LoopArtifactPaths.GetRunPath(_paths, loopId, runId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadRunAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<LoopRunRecord>> ListAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var safeLoopId = LoopArtifactPaths.ValidateArtifactId(loopId);
        var loopRunsPath = Path.Combine(_paths.LoopRunsPath, safeLoopId);
        if (!Directory.Exists(loopRunsPath))
        {
            return [];
        }

        var runs = new List<LoopRunRecord>();
        foreach (var path in Directory.EnumerateFiles(loopRunsPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            runs.Add(await ReadRunAsync(path, cancellationToken));
        }

        return runs.OrderByDescending(run => run.StartedAtUtc).ThenBy(run => run.RunId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<LoopRunRecord> ReadRunAsync(string path, CancellationToken cancellationToken)
    {
        LoopRunRecord? run;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            run = await JsonSerializer.DeserializeAsync<LoopRunRecord>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Loop run `{path}` contains invalid JSON or unsupported enum values.", exception);
        }

        if (run is null)
        {
            throw new FormatException($"Loop run `{path}` was empty.");
        }

        ValidateRun(run);
        return run;
    }

    private static void ValidateRun(LoopRunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.SchemaVersion != LoopRunRecord.CurrentSchemaVersion)
        {
            throw new FormatException($"Unsupported loop run schema version `{run.SchemaVersion}`.");
        }

        LoopArtifactPaths.ValidateArtifactId(run.RunId);
        LoopArtifactPaths.ValidateArtifactId(run.LoopId);
        ArgumentException.ThrowIfNullOrWhiteSpace(run.RoleId);
        var surface = RuntimeSurfaceId.Create(run.Surface);
        if (!string.Equals(run.Surface, surface.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException("Loop run surface must be a canonical runtime surface id.", nameof(run));
        }

        ValidateEnum(run.Status, nameof(run.Status));
        ValidateEnum(run.Trigger, nameof(run.Trigger));
        if (run.Metadata is null || run.Metadata.Keys.Any(string.IsNullOrWhiteSpace) || run.Metadata.Values.Any(value => value is null))
        {
            throw new ArgumentException("Loop run metadata must be present and contain non-empty keys.", nameof(run));
        }
    }

    private static void ValidateEnum<TEnum>(TEnum value, string name) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value) == 0)
        {
            throw new FormatException($"Loop run has unsupported {name} value `{value}`.");
        }
    }
}
