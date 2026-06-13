using EmbodySense.Core.Tools.Models;

namespace EmbodySense.Core.Tools;

public interface IToolApprovalPrompt
{
    Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default);
}
