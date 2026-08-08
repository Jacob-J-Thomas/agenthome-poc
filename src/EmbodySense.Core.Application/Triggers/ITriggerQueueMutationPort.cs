using EmbodySense.Core.Application.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Defines the composition-owned durable queue mutation boundary.</summary>
public interface ITriggerQueueMutationPort
{
    /// <summary>Commits application-created admission evidence atomically or fails closed.</summary>
    /// <param name="request">The internally constructed commit request.</param>
    /// <param name="cancellationToken">A token honored only before staging begins; cancellation after publication is resolved through exact retry.</param>
    /// <returns>The durable queue outcome.</returns>
    Task<TriggerQueueAdmissionResult> CommitAsync(TriggerQueueCommitRequest request, CancellationToken cancellationToken = default);
}
