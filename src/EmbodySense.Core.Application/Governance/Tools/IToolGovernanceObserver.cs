using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Receives ordered governance milestones for a tool request.
/// </summary>
public interface IToolGovernanceObserver
{
    /// <summary>
    /// Observes that a request is waiting for human approval.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <param name="request">The request.</param>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ObserveApprovalRequestAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>
    /// Observes the terminal authority and approval decision before actuation or rejection.
    /// </summary>
    /// <param name="requestId">The request ID.</param>
    /// <param name="request">The request.</param>
    /// <param name="resolvedPath">The resolved path.</param>
    /// <param name="evidence">The evidence.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ObserveDecisionAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Observes the final tool outcome and its retention evidence.
    /// </summary>
    /// <param name="result">The result.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    Task ObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
