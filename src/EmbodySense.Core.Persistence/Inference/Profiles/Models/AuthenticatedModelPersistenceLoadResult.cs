using EmbodySense.Core.Persistence.Capabilities.Models;

namespace EmbodySense.Core.Persistence.Inference.Profiles.Models;

internal sealed record AuthenticatedModelPersistenceLoadResult<TDocument>(
    TDocument? Document,
    TDocument? Pending,
    CapabilityCatalogTrustState? Trust,
    AuthenticatedModelPersistenceDisposition Disposition) where TDocument : class;
