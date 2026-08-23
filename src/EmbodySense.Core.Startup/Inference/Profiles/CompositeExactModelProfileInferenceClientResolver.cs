using EmbodySense.Core.Application.Inference.Profiles;
using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Routes exact model-profile resolution across bounded replaceable adapters without ambiguous ownership.</summary>
public sealed class CompositeExactModelProfileInferenceClientResolver : IExactModelProfileInferenceClientResolver
{
    private const int MaximumResolvers = 32;
    private readonly IReadOnlyList<IExactModelProfileInferenceClientResolver> _resolvers;

    /// <summary>Creates a deterministic resolver set.</summary>
    public CompositeExactModelProfileInferenceClientResolver(IEnumerable<IExactModelProfileInferenceClientResolver> resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        var retained = resolvers.Take(MaximumResolvers + 1).ToArray();
        if (retained.Length is < 1 or > MaximumResolvers || retained.Any(resolver => resolver is null))
        {
            throw new ArgumentException($"Choose between one and {MaximumResolvers} non-null model-profile resolvers.", nameof(resolvers));
        }
        _resolvers = Array.AsReadOnly(retained);
    }

    /// <inheritdoc />
    public async Task<ExactModelProfileInferenceClientResolution> ResolveAsync(
        ExactModelProfileInferenceClientRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = new List<ExactModelProfileInferenceClientResolution>();
        var unavailable = false;
        foreach (var resolver in _resolvers)
        {
            ExactModelProfileInferenceClientResolution? result;
            try
            {
                result = await resolver.ResolveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await DisposeAllAsync(resolved).ConfigureAwait(false);
                throw;
            }
            catch
            {
                result = null;
            }

            if (result?.Status == ExactModelProfileInferenceClientResolutionStatus.Resolved && result.Lease is not null)
            {
                resolved.Add(result);
            }
            else if (result?.Status != ExactModelProfileInferenceClientResolutionStatus.Ineligible || result.Lease is not null)
            {
                unavailable = true;
                if (result?.Lease is not null)
                {
                    try
                    {
                        await result.Lease.DisposeAsync().ConfigureAwait(false);
                    }
                    catch
                    {
                        // The composite remains unavailable; no suspicious lease is returned to execution.
                    }
                }
            }
        }

        if (!unavailable && resolved.Count == 1)
        {
            return resolved[0];
        }

        await DisposeAllAsync(resolved).ConfigureAwait(false);
        return new ExactModelProfileInferenceClientResolution(
            unavailable || resolved.Count > 1
                ? ExactModelProfileInferenceClientResolutionStatus.Unavailable
                : ExactModelProfileInferenceClientResolutionStatus.Ineligible,
            null);
    }

    private static async Task DisposeAllAsync(IEnumerable<ExactModelProfileInferenceClientResolution> resolutions)
    {
        foreach (var resolution in resolutions)
        {
            try
            {
                if (resolution.Lease is not null)
                {
                    await resolution.Lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Disposal failure cannot turn ambiguous adapter ownership into a dispatchable resolution.
            }
        }
    }
}
