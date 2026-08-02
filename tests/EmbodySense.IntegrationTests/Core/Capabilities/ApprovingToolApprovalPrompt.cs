using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.IntegrationTests.Core.Capabilities;

internal sealed class ApprovingToolApprovalPrompt : IToolApprovalPrompt
{
    public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToolApprovalResponse.Approve("ordering-test", "approved for deterministic ordering test"));
    }
}
