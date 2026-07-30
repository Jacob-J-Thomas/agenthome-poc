using EmbodySense.Core.Common.Governance.Tools.Models;

namespace EmbodySense.Core.Application.Governance.Tools;

/// <summary>
/// Resolves a tool target and evaluates its effective file-system permission.
/// </summary>
public interface IToolPermissionService
{
    /// <summary>
    /// Canonicalizes and evaluates a request against workspace containment, reparse-point, and policy rules.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The resolved target, permission target, operation, decision, and policy evidence hash.</returns>
    ToolPermissionCheck Evaluate(ToolRequest request);
}
