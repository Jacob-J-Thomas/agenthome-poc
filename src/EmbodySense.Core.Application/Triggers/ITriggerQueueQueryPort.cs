using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Exposes bounded queue and fairness evidence without selection or dispatch.</summary>
public interface ITriggerQueueQueryPort
{
    /// <summary>Loads a validated snapshot and durably terminalizes elapsed entries when persistence capacity is available.</summary>
    /// <param name="observedAtUtc">The exact UTC observation instant.</param>
    /// <param name="cancellationToken">A token honored before any durable expiry commit begins.</param>
    /// <returns>The bounded durable snapshot; <see cref="TriggerQueueSnapshot.PersistenceBackpressured"/> indicates that elapsed transitions could not be committed.</returns>
    Task<TriggerQueueSnapshot> GetSnapshotAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken = default);
}
