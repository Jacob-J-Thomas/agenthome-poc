using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class InvocationAuthorityApprovalPrompt : IAgentToolApprovalPrompt
{
    public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((true, "test-authority", "approved for deterministic authority revalidation coverage"));
    }
}
