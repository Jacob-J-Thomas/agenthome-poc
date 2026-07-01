using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Loops;

public sealed class LoopDefinitionStore : ILoopDefinitionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) } };
    private readonly WorkspacePaths _paths;

    public LoopDefinitionStore(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    public async Task SaveAsync(LoopDefinition definition, CancellationToken cancellationToken = default)
    {
        ValidateDefinition(definition);

        Directory.CreateDirectory(_paths.LoopDefinitionsPath);
        var json = JsonSerializer.Serialize(definition, JsonOptions) + Environment.NewLine;
        await LoopArtifactFileWriter.WriteTextAsync(LoopArtifactPaths.GetDefinitionPath(_paths, definition.Id), json, cancellationToken);
    }

    public async Task<LoopDefinition?> LoadAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var path = LoopArtifactPaths.GetDefinitionPath(_paths, loopId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadDefinitionAsync(path, cancellationToken);
    }

    public async Task<IReadOnlyList<LoopDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_paths.LoopDefinitionsPath))
        {
            return [];
        }

        var definitions = new List<LoopDefinition>();
        foreach (var path in Directory.EnumerateFiles(_paths.LoopDefinitionsPath, "*.json", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            definitions.Add(await ReadDefinitionAsync(path, cancellationToken));
        }

        return definitions.OrderBy(definition => definition.Id, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task<LoopDefinition> ReadDefinitionAsync(string path, CancellationToken cancellationToken)
    {
        LoopDefinition? definition;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            definition = await JsonSerializer.DeserializeAsync<LoopDefinition>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Loop definition `{path}` contains invalid JSON or unsupported enum values.", exception);
        }

        if (definition is null)
        {
            throw new FormatException($"Loop definition `{path}` was empty.");
        }

        ValidateDefinition(definition);
        return definition;
    }

    private static void ValidateDefinition(LoopDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.SchemaVersion != LoopDefinition.CurrentSchemaVersion)
        {
            throw new FormatException($"Unsupported loop definition schema version `{definition.SchemaVersion}`.");
        }

        LoopArtifactPaths.ValidateArtifactId(definition.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Description);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.RoleId);
        ValidateEnum(definition.Trigger, nameof(definition.Trigger));
        ValidateEnum(definition.MemoryScope, nameof(definition.MemoryScope));
        ValidateEnum(definition.ReviewPolicy, nameof(definition.ReviewPolicy));
        ValidateEnum(definition.FailurePolicy, nameof(definition.FailurePolicy));
        ValidateEnum(definition.State, nameof(definition.State));
        ValidateEnum(definition.EditMode, nameof(definition.EditMode));
        if (definition.CapabilityIds is null || definition.CapabilityIds.Length == 0 || definition.CapabilityIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Loop definitions must include at least one capability id.", nameof(definition));
        }

        if (definition.Graph is null)
        {
            throw new FormatException("Loop definitions must include a graph.");
        }

        var graphFailure = definition.Graph.GetValidationFailure();
        if (graphFailure is not null)
        {
            throw new FormatException(graphFailure);
        }
    }

    private static void ValidateEnum<TEnum>(TEnum value, string name) where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value) == 0)
        {
            throw new FormatException($"Loop definition has unsupported {name} value `{value}`.");
        }
    }
}
