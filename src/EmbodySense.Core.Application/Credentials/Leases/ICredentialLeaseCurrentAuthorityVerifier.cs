using EmbodySense.Core.Application.Credentials.Leases.Models;
using EmbodySense.Core.Common.Credentials.Leases.Models;

namespace EmbodySense.Core.Application.Credentials.Leases;

/// <summary>Revalidates exact current grant, delegation, role, loop, effect, capability, profile, target, actor, and runtime authority.</summary>
public interface ICredentialLeaseCurrentAuthorityVerifier
{
    /// <summary>Reads authoritative server-owned evidence for the supplied untrusted value-free intent.</summary>
    Task<CredentialLeaseCurrentVerificationResult> VerifyAsync(CredentialLeaseIntent intent, CancellationToken cancellationToken = default);
}
