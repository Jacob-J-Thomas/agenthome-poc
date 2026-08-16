using EmbodySense.Core.Startup.Runtime.Models;

namespace EmbodySense.Core.Startup.Runtime;

/// <summary>Projects one surface-owned authenticated-event source into canonical governed Wait verification.</summary>
public interface IAgentRuntimeAuthenticatedWakeVerifier
{
    /// <summary>Verifies one exact event-evidence hash for the immutable sleeping checkpoint.</summary>
    /// <param name="request">The exact checkpoint, event reference, and submitted evidence coordinates.</param>
    /// <param name="cancellationToken">The token used before durable wake intent is retained.</param>
    /// <returns>The closed verification outcome, or <see langword="null"/> for a contract violation.</returns>
    Task<AgentRuntimeAuthenticatedWakeVerificationResult?> VerifyAsync(
        AgentRuntimeAuthenticatedWakeVerificationRequest request,
        CancellationToken cancellationToken = default);
}
