using EmbodySense.Core.Application.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Revalidates dynamic authority immediately before an approved workspace mutation is actuated.
/// </summary>
public interface IToolActuationAuthorityRevalidator
{
    /// <summary>
    /// Re-evaluates the request after permission and human-approval checks to close time-of-check/time-of-use gaps.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The latest authority decision and audit metadata.</returns>
    Task<ToolActuationAuthorityRevalidation> RevalidateAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
