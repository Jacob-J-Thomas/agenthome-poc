using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.LocalWorkspace.Models;

namespace EmbodySense.Core.Application.LocalWorkspace;

/// <summary>Projects structured ToolBroker mutations into the canonical durable actuator/effect protocol.</summary>
public interface IGovernedWorkspaceMutationToolExecutor
{
    /// <summary>Executes one semantic workspace mutation only when complete admitted run identity is available.</summary>
    Task<LocalWorkspaceResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
