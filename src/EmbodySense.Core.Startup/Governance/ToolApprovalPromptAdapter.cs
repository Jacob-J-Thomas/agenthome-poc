using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Startup.Governance;

internal sealed class ToolApprovalPromptAdapter : IToolApprovalPrompt
{
    private readonly IAgentToolApprovalPrompt _approvalPrompt;

    public ToolApprovalPromptAdapter(IAgentToolApprovalPrompt approvalPrompt)
    {
        ArgumentNullException.ThrowIfNull(approvalPrompt);

        _approvalPrompt = approvalPrompt;
    }

    public async Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _approvalPrompt.RequestApprovalAsync(AgentToolApprovalRequest.FromToolApprovalRequest(request), cancellationToken);
        return response.Approved
            ? ToolApprovalResponse.Approve(response.DecisionBy, response.Detail)
            : ToolApprovalResponse.Reject(response.DecisionBy, response.Detail);
    }
}
