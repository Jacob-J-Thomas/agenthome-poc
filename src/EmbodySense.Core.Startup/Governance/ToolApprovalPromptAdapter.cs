using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Startup.Governance;
using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Startup.Governance;

internal sealed class ToolApprovalPromptAdapter : IToolApprovalPrompt
{
    private readonly IAgentToolApprovalPrompt _approvalPrompt;

    /// <summary>
    /// Initializes an application-layer approval adapter over the interface-owned prompt.
    /// </summary>
    /// <param name="approvalPrompt">The interface prompt that owns the decision interaction.</param>
    public ToolApprovalPromptAdapter(IAgentToolApprovalPrompt approvalPrompt)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);

        _approvalPrompt = approvalPrompt;
    }

    /// <summary>
    /// Projects an application request to the public startup contract and maps the returned
    /// decision back to an application response.
    /// </summary>
    /// <param name="request">The governed application-layer request.</param>
    /// <param name="cancellationToken">The token used to cancel the pending prompt.</param>
    /// <returns>A task whose result preserves the approval decision, decision maker, and detail.</returns>
    /// <remarks>Prompt exceptions and cancellation propagate unchanged.</remarks>
    public async Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _approvalPrompt.RequestApprovalAsync(AgentToolApprovalRequest.FromToolApprovalRequest(request), cancellationToken);
        return response.Approved
            ? ToolApprovalResponse.Approve(response.DecisionBy, response.Detail)
            : ToolApprovalResponse.Reject(response.DecisionBy, response.Detail);
    }
}
