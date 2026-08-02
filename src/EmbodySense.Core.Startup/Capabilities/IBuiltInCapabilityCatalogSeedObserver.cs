using EmbodySense.Core.Application.Capabilities.Models;

namespace EmbodySense.Core.Startup.Capabilities;

/// <summary>Observes applied built-in bootstrap transitions after their catalog transactions have committed.</summary>
/// <remarks>The observer is trusted Startup infrastructure that runs between transactions. It receives immutable catalog evidence and no assignment or authority. Exceptions stop the current seed invocation after the observed transaction has committed.</remarks>
public interface IBuiltInCapabilityCatalogSeedObserver
{
    /// <summary>Observes one applied transition after the catalog lock and transaction have completed.</summary>
    /// <param name="entry">The exact committed built-in entry snapshot.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after observation.</returns>
    Task TransitionCommittedAsync(CapabilityCatalogEntry entry, CancellationToken cancellationToken = default);
}
