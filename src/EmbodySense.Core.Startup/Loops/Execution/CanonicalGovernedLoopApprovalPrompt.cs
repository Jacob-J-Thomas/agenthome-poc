using EmbodySense.Core.Application.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Startup.Loops.Execution;

/// <summary>
/// Fails closed when a canonical governed-loop tool request reaches the legacy approval seam.
/// </summary>
internal sealed class CanonicalGovernedLoopApprovalPrompt : IToolApprovalPrompt
{
    public static CanonicalGovernedLoopApprovalPrompt Instance { get; } = new();

    private CanonicalGovernedLoopApprovalPrompt()
    {
    }

    public Task<ToolApprovalResponse> RequestApprovalAsync(ToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToolApprovalResponse.Reject("system.governed-loop", "canonical_governed_loop_approval_unavailable"));
    }
}
