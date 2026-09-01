using EmbodySense.Core.Startup.HumanInput.Models;

namespace EmbodySense.Core.Startup.HumanInput;

/// <summary>Owns bounded process-local opaque Human Input supersede candidates.</summary>
/// <remarks>Entries are never durable authority. Restart clears them, and every lookup rebinds actor, workspace, operation,
/// exact request state, expiry, and candidate integrity before returning canonical terms.</remarks>
public interface IHumanInputSupersedeCandidateRegistry
{
    /// <summary>Stores or reuses one exact candidate registration.</summary>
    /// <param name="candidate">The server-composed candidate and binding.</param>
    /// <param name="candidateKey">The opaque key assigned to the registration.</param>
    /// <returns><see langword="true"/> when the registration is retained; otherwise the finite registry is unavailable.</returns>
    bool TryRegister(HumanInputSupersedeCandidateRegistration candidate, out string candidateKey);

    /// <summary>Resolves one opaque candidate only when every exact lookup binding remains valid.</summary>
    /// <param name="candidateKey">The opaque key supplied by the surface.</param>
    /// <param name="workspaceId">The server-derived workspace identity.</param>
    /// <param name="actor">The server-derived actor identity.</param>
    /// <param name="operationId">The exact operation identity.</param>
    /// <param name="requestId">The exact target request identity.</param>
    /// <param name="expectedLifecycleVersion">The exact optimistic lifecycle version.</param>
    /// <param name="expectedRequestVersionId">The exact immutable request-version identity.</param>
    /// <param name="expectedRequestHash">The exact optimistic request hash.</param>
    /// <param name="now">The trusted lookup time.</param>
    /// <param name="resolution">The exact candidate and grant when available.</param>
    /// <returns><see langword="true"/> only for one valid, unexpired, exactly bound entry.</returns>
    bool TryResolve(string candidateKey, string workspaceId, string actor, string operationId, string requestId, long expectedLifecycleVersion, string expectedRequestVersionId, string expectedRequestHash, DateTimeOffset now, out HumanInputSupersedeCandidateResolution? resolution);
}
