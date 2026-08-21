using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Inference.Profiles.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Composes bounded adapter registries and rejects duplicate profile ownership.</summary>
public sealed class CompositeModelProfileAdapterRegistry : IModelProfileAdapterRegistry
{
    private const int MaximumRegistries = 32;
    private readonly IReadOnlyList<IModelProfileAdapterRegistry> _registries;

    /// <summary>Creates a deterministic registry set.</summary>
    public CompositeModelProfileAdapterRegistry(IEnumerable<IModelProfileAdapterRegistry> registries)
    {
        ArgumentNullException.ThrowIfNull(registries);
        var retained = registries.Take(MaximumRegistries + 1).ToArray();
        if (retained.Length is < 1 or > MaximumRegistries || retained.Any(registry => registry is null))
        {
            throw new ArgumentException($"Choose between one and {MaximumRegistries} non-null model adapter registries.", nameof(registries));
        }
        _registries = Array.AsReadOnly(retained);
    }

    /// <inheritdoc />
    public async Task<ModelProfileAdapterPosture> ReadPostureAsync(
        GovernedModelProfileMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var postures = new List<ModelProfileAdapterPosture>(_registries.Count);
        var invalid = false;
        foreach (var registry in _registries)
        {
            ModelProfileAdapterPosture? result;
            try
            {
                result = await registry.ReadPostureAsync(metadata, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = null;
            }

            if (result is null
                || !Enum.IsDefined(result.Status)
                || result.Status == 0
                || !string.Equals(result.ProfileMetadataHash, metadata.ContentHash, StringComparison.Ordinal)
                || !IsHash(result.RegistryRevisionHash))
            {
                invalid = true;
                continue;
            }
            postures.Add(result!);
        }

        var claimed = postures
            .Where(posture => posture.Status != ModelProfileAdapterPostureStatus.Unregistered)
            .ToArray();
        if (invalid || postures.Count != _registries.Count || claimed.Length > 1)
        {
            return Unavailable(metadata, postures);
        }
        return new ModelProfileAdapterPosture(
            claimed.Length == 1 ? claimed[0].Status : ModelProfileAdapterPostureStatus.Unregistered,
            metadata.ContentHash,
            CompositeRevision(postures));
    }

    private static ModelProfileAdapterPosture Unavailable(
        GovernedModelProfileMetadata metadata,
        IReadOnlyList<ModelProfileAdapterPosture> claimed)
        => new(
            ModelProfileAdapterPostureStatus.Unavailable,
            metadata.ContentHash,
            CompositeRevision(claimed));

    private static string CompositeRevision(
        IReadOnlyList<ModelProfileAdapterPosture> postures)
        => CustomLoopTraceContentHash.Compute(string.Join('\n',
            new[]
            {
                "embodysense.composite-model-adapter-registry.v1",
            }.Concat(postures.Select(posture => posture.RegistryRevisionHash ?? string.Empty))));

    private static bool IsHash(string? value)
        => value is { Length: 64 }
            && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
