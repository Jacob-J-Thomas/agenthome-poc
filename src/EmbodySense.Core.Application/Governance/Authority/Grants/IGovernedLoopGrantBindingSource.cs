using EmbodySense.Core.Application.Governance.Authority.Grants.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Governance.Authority.Grants;

/// <summary>Resolves exact governed-loop publication ownership and assigned capability evidence.</summary>
public interface IGovernedLoopGrantBindingSource
{
    /// <summary>Resolves one exact publication pin without selecting a newer loop revision.</summary>
    /// <param name="pin">The exact publication pin.</param>
    /// <param name="cancellationToken">A token that cancels the source read.</param>
    /// <returns>The exact publication artifact, owner pin, capability identifiers, and evidence posture.</returns>
    Task<GovernedLoopGrantBindingResolution> ResolveAsync(GovernedLoopRevisionPublicationPin? pin, CancellationToken cancellationToken = default);
}
