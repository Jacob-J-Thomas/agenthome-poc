using EmbodySense.Core.Application.Loops.Revisions.Models;
using EmbodySense.Core.Common.Loops.Revisions.Models;

namespace EmbodySense.Core.Application.Loops.Revisions;

/// <summary>Resolves only caller-supplied exact publication pins and never follows a graph's current head.</summary>
public interface IGovernedLoopPublishedRevisionSource
{
    /// <summary>Resolves one exact pin under the shared reentrant workspace authority fence.</summary>
    /// <param name="pin">The exact immutable publication pin to resolve.</param>
    /// <param name="cancellationToken">The cancellation token used while resolving.</param>
    /// <returns>Exact current and historical evidence without replacement selection.</returns>
    Task<GovernedLoopPublishedRevisionResolution> ResolveAsync(
        GovernedLoopRevisionPublicationPin? pin,
        CancellationToken cancellationToken = default);
}
