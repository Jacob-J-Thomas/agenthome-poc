using EmbodySense.Core.Startup.Triggers.Models;

namespace EmbodySense.Core.Startup.Triggers;

/// <summary>Defines the trusted Startup composition seam for exact current trigger-dispatch evidence.</summary>
/// <remarks>Implementations must re-read current loop, assignment, capability, authority, actor, workspace, and temporal state; selected envelope fields are evidence, never grants.</remarks>
public interface ITriggerWorkerCurrentEvidenceAuthorizer
{
    /// <summary>Revalidates the selected evidence at the supplied UTC instant.</summary>
    /// <param name="input">The immutable selected evidence to compare with trusted current sources.</param>
    /// <param name="evaluatedAtUtc">The exact UTC evaluation instant.</param>
    /// <param name="cancellationToken">A token honored before durable dispatch intent.</param>
    /// <returns>The closed decision and exact proof hash.</returns>
    Task<TriggerWorkerAuthorizationResponse> AuthorizeAsync(TriggerWorkerCurrentEvidenceInput input, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default);
}
