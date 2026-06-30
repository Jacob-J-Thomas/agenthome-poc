using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public interface IToolApprovalPrompt
{
    Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);
}
