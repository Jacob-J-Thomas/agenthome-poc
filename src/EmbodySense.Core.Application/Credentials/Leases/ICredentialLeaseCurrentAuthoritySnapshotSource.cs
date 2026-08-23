using EmbodySense.Core.Application.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Reads one complete current credential-authority snapshot from canonical server-owned truth.</summary>
public interface ICredentialLeaseCurrentAuthoritySnapshotSource
{
    /// <summary>Resolves the current value-free snapshot for one untrusted stable credential-use identity.</summary>
    /// <remarks>The implementation must authenticate and re-read the canonical admission, run, role, loop, grant, delegation, effect, capability, profile, and target sources. It must never echo fields from the supplied identity into an authorization result.</remarks>
    Task<CredentialLeaseCurrentAuthoritySnapshot> ReadAsync(string credentialUseOperationId, long credentialUseGeneration, CancellationToken cancellationToken = default);
}
