using EmbodySense.Core.Application.HumanInput.Publication.Models;

namespace EmbodySense.Core.Application.HumanInput.Publication;

/// <summary>Reconciles one durable Human Input waiting checkpoint into the canonical request lifecycle ledger.</summary>
public interface IHumanInputRequestPublicationService
{
    /// <summary>Probes whether the canonical request ledger can safely establish one current state.</summary>
    /// <param name="cancellationToken">Cancels before the bounded read begins.</param>
    /// <returns>A closed health result that distinguishes unavailable storage from corrupt evidence.</returns>
    Task<HumanInputRequestPublicationHealthResult> ProbeAsync(CancellationToken cancellationToken = default);

    /// <summary>Publishes or exactly replays one immutable checkpoint-backed Human Input request.</summary>
    /// <param name="request">The exact canonical run and checkpoint identity to reread and reconcile.</param>
    /// <param name="cancellationToken">Cancels before the bounded publication attempt begins.</param>
    /// <returns>A closed result that never exposes a request before durable lifecycle proof.</returns>
    Task<HumanInputRequestPublicationResult> PublishAsync(HumanInputRequestPublicationRequest? request, CancellationToken cancellationToken = default);
}
