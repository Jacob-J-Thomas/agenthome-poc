using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

public interface IToolGovernanceObserver
{
    Task ObserveApprovalRequestAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default) => Task.CompletedTask;

    Task ObserveDecisionAsync(string requestId, ToolRequest request, string resolvedPath, ToolGovernanceEvidence evidence, CancellationToken cancellationToken = default);

    Task ObserveOutcomeAsync(ToolResult result, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
