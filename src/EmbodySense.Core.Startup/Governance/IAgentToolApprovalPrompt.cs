namespace EmbodySense.Core.Startup.Governance;

public interface IAgentToolApprovalPrompt
{
    Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default);
}
