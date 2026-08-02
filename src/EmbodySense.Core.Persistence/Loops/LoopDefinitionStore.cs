using EmbodySense.Core.Common.Loops;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Application.Capabilities;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Common.Loops.Models;
using EmbodySense.Core.Common.Capabilities;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Capabilities;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Persists version-1 default-loop definitions as one JSON artifact per loop identifier.
/// </summary>
/// <remarks>
/// Each save validates the complete definition and atomically replaces the target file through
/// <see cref="LoopArtifactFileWriter"/>. Loads return <see langword="null"/> for a missing artifact; malformed JSON,
/// unsupported enum values, invalid identities, and file I/O failures are surfaced to the caller. Listings are deterministic
/// by loop identifier.
/// </remarks>
public sealed class LoopDefinitionStore : ILoopDefinitionStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) } };
    private readonly WorkspacePaths _paths;
    private readonly ICapabilityAuthorityTransaction _authorityTransaction;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopDefinitionStore"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    /// <param name="authorityTransaction">The optional shared workspace capability-authority transaction.</param>
    public LoopDefinitionStore(WorkspacePaths paths, ICapabilityAuthorityTransaction? authorityTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _authorityTransaction = authorityTransaction ?? new CapabilityAuthorityTransaction(paths);
    }

    /// <summary>
    /// Validates and atomically writes the canonical definition artifact.
    /// </summary>
    /// <param name="definition">The definition.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public Task SaveAsync(LoopDefinition definition, CancellationToken cancellationToken = default) => _authorityTransaction.ExecuteAsync(transactionCancellationToken => SaveCoreAsync(definition, transactionCancellationToken), cancellationToken);

    private async Task<bool> SaveCoreAsync(LoopDefinition definition, CancellationToken cancellationToken)
    {
        ValidateDefinition(definition);

        Directory.CreateDirectory(_paths.LoopDefinitionsPath);
        var json = JsonSerializer.Serialize(definition, _jsonOptions) + Environment.NewLine;
        await LoopArtifactFileWriter.WriteTextAsync(LoopArtifactPaths.GetDefinitionPath(_paths, definition.Id), json, cancellationToken);
        return true;
    }

    /// <summary>
    /// Loads and validates one definition by its canonical identifier.
    /// </summary>
    /// <param name="loopId">The loop ID.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The definition, or <see langword="null"/> when its artifact does not exist.</returns>
    public async Task<LoopDefinition?> LoadAsync(string loopId, CancellationToken cancellationToken = default)
    {
        var path = LoopArtifactPaths.GetDefinitionPath(_paths, loopId);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadDefinitionAsync(path, cancellationToken);
    }

    /// <summary>
    /// Loads every top-level definition artifact in deterministic identifier order.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>All validated definitions, or an empty collection when the definitions directory does not exist.</returns>
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
            definition = await JsonSerializer.DeserializeAsync<LoopDefinition>(stream, _jsonOptions, cancellationToken);
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

        if (!CapabilityDependencyManifestValidator.Validate(definition.CapabilityRequirements).IsValid)
        {
            throw new FormatException("Loop definitions must include a valid bounded capability requirement manifest.");
        }

        if (string.Equals(definition.Id, BuiltInLoopIds.DefaultConversation, StringComparison.Ordinal))
        {
            var expected = LoopCapabilityRequirements.CreateDefaultConversationManifest();
            if (!CapabilityDependencyManifestHash.TryCompute(definition.CapabilityRequirements, out var actualHash, out _)
                || !CapabilityDependencyManifestHash.TryCompute(expected, out var expectedHash, out _)
                || !string.Equals(actualHash!.Value, expectedHash!.Value, StringComparison.Ordinal))
            {
                throw new FormatException("The default-conversation capability requirements must match the server-owned mapping.");
            }
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
