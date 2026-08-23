using EmbodySense.Core.Application.Inference.Profiles.Models;

namespace EmbodySense.Core.Application.Inference.Profiles;

/// <summary>Resolves the trusted host default without reading ambient workspace or browser values.</summary>
public interface IModelProfileDefaultSource
{
    /// <summary>Reads the exact current configured default.</summary>
    Task<ModelProfileDefaultReadResult> ReadAsync(CancellationToken cancellationToken = default);
}
