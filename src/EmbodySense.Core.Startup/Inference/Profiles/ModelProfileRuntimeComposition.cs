using EmbodySense.Core.Application.Inference.Profiles;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Builds one bounded, duplicate-rejecting model-profile runtime from replaceable server-owned providers.</summary>
public sealed class ModelProfileRuntimeComposition
{
    private const int MaximumProviders = 32;

    private ModelProfileRuntimeComposition(
        IModelProfileMetadataSource metadataSource,
        IModelProfileAdapterRegistry adapterRegistry,
        IExactModelProfileInferenceClientResolver clientResolver)
    {
        MetadataSource = metadataSource;
        AdapterRegistry = adapterRegistry;
        ClientResolver = clientResolver;
    }

    /// <summary>Gets the composed metadata source.</summary>
    public IModelProfileMetadataSource MetadataSource { get; }

    /// <summary>Gets the composed adapter posture registry.</summary>
    public IModelProfileAdapterRegistry AdapterRegistry { get; }

    /// <summary>Gets the composed exact-client resolver.</summary>
    public IExactModelProfileInferenceClientResolver ClientResolver { get; }

    /// <summary>Composes the required configured provider and up to thirty-one additional server-owned providers.</summary>
    public static ModelProfileRuntimeComposition Create(
        ModelProfileRuntimeProvider configuredProvider,
        IEnumerable<ModelProfileRuntimeProvider>? additionalProviders = null)
    {
        ArgumentNullException.ThrowIfNull(configuredProvider);
        var additional = (additionalProviders ?? [])
            .Take(MaximumProviders)
            .ToArray();
        if (additional.Length >= MaximumProviders || additional.Any(provider => provider is null))
        {
            throw new ArgumentException($"Choose no more than {MaximumProviders - 1} non-null additional model-profile providers.", nameof(additionalProviders));
        }

        var providers = new[] { configuredProvider }.Concat(additional).ToArray();
        var metadata = new CompositeModelProfileMetadataSource(providers.Select(provider => provider.MetadataSource));
        var adapters = new CompositeModelProfileAdapterRegistry(providers.Select(provider => provider.AdapterRegistry));
        var resolvers = providers
            .Select(provider => provider.ClientResolverFactory(adapters)
                ?? throw new InvalidOperationException("A model-profile resolver factory returned no resolver."))
            .ToArray();
        return new ModelProfileRuntimeComposition(
            metadata,
            adapters,
            new CompositeExactModelProfileInferenceClientResolver(resolvers));
    }
}
