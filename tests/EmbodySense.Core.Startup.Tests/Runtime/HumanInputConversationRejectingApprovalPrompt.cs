using EmbodySense.Core.Startup.Governance;

namespace EmbodySense.Core.Startup.Tests.Runtime;

internal sealed class HumanInputConversationRejectingApprovalPrompt : IAgentToolApprovalPrompt
{
    public Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult((false, "test", "No approval is required by Human Input conversation tests."));
    }
}
