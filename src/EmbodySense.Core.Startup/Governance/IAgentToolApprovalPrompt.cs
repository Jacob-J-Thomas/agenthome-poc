using EmbodySense.Core.Startup.Governance;
namespace EmbodySense.Core.Startup.Governance;

/// <summary>
/// Defines the interface-owned decision boundary used when a governed tool request requires
/// explicit human approval.
/// </summary>
public interface IAgentToolApprovalPrompt
{
    /// <summary>
    /// Requests one approval decision for the supplied governed tool operation.
    /// </summary>
    /// <param name="request">The policy-derived request details to present to the decision maker.</param>
    /// <param name="cancellationToken">The token used to cancel the pending decision.</param>
    /// <returns>
    /// A task whose result contains the allow-or-reject decision, the identity that made it, and
    /// an audit-ready explanation.
    /// </returns>
    /// <remarks>
    /// Cancellation and prompt failures propagate to the caller; they are not converted into
    /// approval decisions by the startup adapter.
    /// </remarks>
    Task<(bool Approved, string DecisionBy, string Detail)> RequestApprovalAsync(AgentToolApprovalRequest request, CancellationToken cancellationToken = default);
}
