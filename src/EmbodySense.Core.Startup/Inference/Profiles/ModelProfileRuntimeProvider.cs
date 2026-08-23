using EmbodySense.Core.Application.Inference.Profiles;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Supplies one bounded server-owned model-profile metadata, posture, and exact-client adapter set to Startup.</summary>
public sealed record ModelProfileRuntimeProvider
{
    /// <summary>Creates one replaceable server-owned profile provider.</summary>
    public ModelProfileRuntimeProvider(
        IModelProfileMetadataSource metadataSource,
        IModelProfileAdapterRegistry adapterRegistry,
        Func<IModelProfileAdapterRegistry, IExactModelProfileInferenceClientResolver> clientResolverFactory)
    {
        MetadataSource = metadataSource ?? throw new ArgumentNullException(nameof(metadataSource));
        AdapterRegistry = adapterRegistry ?? throw new ArgumentNullException(nameof(adapterRegistry));
        ClientResolverFactory = clientResolverFactory ?? throw new ArgumentNullException(nameof(clientResolverFactory));
    }

    /// <summary>Gets the profile metadata source.</summary>
    public IModelProfileMetadataSource MetadataSource { get; }

    /// <summary>Gets the current adapter posture registry.</summary>
    public IModelProfileAdapterRegistry AdapterRegistry { get; }

    /// <summary>Gets the factory that binds the provider's exact client resolver to the final duplicate-rejecting registry revision.</summary>
    public Func<IModelProfileAdapterRegistry, IExactModelProfileInferenceClientResolver> ClientResolverFactory { get; }
}
