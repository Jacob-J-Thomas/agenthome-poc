using EmbodySense.Core.Common.Governance.Tools.Models;
using EmbodySense.Core.Common.Governance.Permissions.Models;

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

    /// <summary>
    /// Canonicalizes one exact regular-file mutation target and evaluates the caller-supplied server-derived operation class.
    /// </summary>
    /// <param name="request">The workspace mutation request whose target is evaluated.</param>
    /// <param name="operation">The exact create, append, modify, or delete class derived from retained native state.</param>
    /// <returns>The resolved target, parent policy target, exact operation, decision, and current policy evidence hash.</returns>
    ToolPermissionCheck EvaluateExactFileMutation(ToolRequest request, FileSystemOperation operation);
}
