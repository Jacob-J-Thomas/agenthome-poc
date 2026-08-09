using EmbodySense.Core.Application.Triggers.Models;
using EmbodySense.Core.Common.Triggers.Models;

namespace EmbodySense.Core.Application.Triggers;

/// <summary>Revalidates composition-owned current loop, assignment, capability, authority, and temporal evidence.</summary>
public interface ITriggerDispatchAuthorizer
{
    /// <summary>Revalidates one selected envelope immediately before durable dispatch intent.</summary>
    /// <param name="envelope">The selected immutable envelope evidence, which does not grant authority.</param>
    /// <param name="evaluatedAtUtc">The exact UTC revalidation instant.</param>
    /// <param name="cancellationToken">A token honored before durable dispatch intent.</param>
    /// <returns>The closed current-evidence posture and exact proof binding.</returns>
    Task<TriggerDispatchAuthorization> AuthorizeAsync(TriggerDeliveryEnvelope envelope, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default);
}
