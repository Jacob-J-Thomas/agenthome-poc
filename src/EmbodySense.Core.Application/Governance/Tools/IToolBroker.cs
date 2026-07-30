using EmbodySense.Core.Common.Governance.Tools;
using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Governs model-accessible workspace commands through authority, permission, approval, audit, and retention checks.
/// </summary>
public interface IToolBroker
{
    /// <summary>
    /// Gets the commands admitted by the active loop definition.
    /// </summary>
    /// <value>The available commands tool commands.</value>
    IReadOnlyList<ToolCommand> AvailableCommands { get; }

    /// <summary>
    /// Evaluates and, when authorized, executes a workspace command through the governance pipeline.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The terminal result together with governance and retention evidence.</returns>
    Task<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken cancellationToken = default);
}
