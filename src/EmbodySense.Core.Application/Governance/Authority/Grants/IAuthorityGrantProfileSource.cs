using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Authority.Grants.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves one exact authority-profile pin against current lifecycle truth.</summary>
public interface IAuthorityGrantProfileSource
{
    /// <summary>Resolves one exact profile revision without following a successor.</summary>
    /// <param name="pin">The exact profile revision and canonical hash.</param>
    /// <param name="evaluatedAtUtc">The trusted UTC boundary instant.</param>
    /// <param name="cancellationToken">A token that cancels the source read.</param>
    /// <returns>The exact current posture and value-free evidence digest.</returns>
    Task<AuthorityGrantProfileResolution> ResolveAsync(AuthorityGrantProfilePin? pin, DateTimeOffset evaluatedAtUtc, CancellationToken cancellationToken = default);
}
