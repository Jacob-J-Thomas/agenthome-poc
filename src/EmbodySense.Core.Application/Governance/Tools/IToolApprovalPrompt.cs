using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Obtains an explicit human decision for a tool request whose policy requires approval.
/// </summary>
public interface IToolApprovalPrompt
{
    /// <summary>
    /// Requests a human approval decision for the governed operation.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The approval or rejection together with decision provenance.</returns>
    Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);
}
