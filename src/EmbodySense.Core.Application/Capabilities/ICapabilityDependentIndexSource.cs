using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Application.Capabilities;

/// <summary>Reads one domain-owned slice of the capability dependent index without mutating its source.</summary>
public interface ICapabilityDependentIndexSource
{
    /// <summary>Gets the stable diagnostic source name.</summary>
    string Name { get; }

    /// <summary>Reads the complete bounded source slice or throws when the source cannot be proved.</summary>
    Task<IReadOnlyList<CapabilityDependent>> ReadAsync(CancellationToken cancellationToken = default);
}
