using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;
using EmbodySense.Core.Common.Capabilities;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Composes bounded replaceable profile metadata sources without allowing duplicate ownership.</summary>
public sealed class CompositeModelProfileMetadataSource : IModelProfileMetadataSource
{
    private const int MaximumSources = 32;
    private readonly IReadOnlyList<IModelProfileMetadataSource> _sources;

    /// <summary>Creates a deterministic source set.</summary>
    public CompositeModelProfileMetadataSource(IEnumerable<IModelProfileMetadataSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var retained = sources.Take(MaximumSources + 1).ToArray();
        if (retained.Length is < 1 or > MaximumSources || retained.Any(source => source is null))
        {
            throw new ArgumentException($"Choose between one and {MaximumSources} non-null profile metadata sources.", nameof(sources));
        }
        _sources = Array.AsReadOnly(retained);
    }

    /// <inheritdoc />
    public async Task<ModelProfileSourceReadResult> ReadAsync(
        CapabilityId profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ModelProfileSourceReadResult? found = null;
        var unavailable = false;
        foreach (var source in _sources)
        {
            ModelProfileSourceReadResult? result;
            try
            {
                result = await source.ReadAsync(profileId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result = null;
            }

            if (result?.Status == ModelProfileSourceReadStatus.Found)
            {
                if (found is not null)
                {
                    return Unavailable();
                }
                found = result;
            }
            else if (result?.Status != ModelProfileSourceReadStatus.NotFound)
            {
                unavailable = true;
            }
        }

        return unavailable
            ? Unavailable()
            : found ?? new ModelProfileSourceReadResult(ModelProfileSourceReadStatus.NotFound, null, null);
    }

    private static ModelProfileSourceReadResult Unavailable()
        => new(ModelProfileSourceReadStatus.Unavailable, null, null);
}
