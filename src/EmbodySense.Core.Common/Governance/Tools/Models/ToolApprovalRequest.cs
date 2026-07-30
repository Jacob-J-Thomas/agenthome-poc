using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents a tool approval request.
/// </summary>
/// <param name="RequestId">The request ID.</param>
/// <param name="ToolRequest">The tool request.</param>
/// <param name="ResolvedPath">The resolved path.</param>
/// <param name="Operation">The operation.</param>
/// <param name="PermissionEvaluation">The permission evaluation.</param>
/// <param name="PermissionPolicyHash">The permission policy hash.</param>
public sealed record ToolApprovalRequest(
    string RequestId,
    ToolRequest ToolRequest,
    string ResolvedPath,
    FileSystemOperation Operation,
    PermissionEvaluation PermissionEvaluation,
    string? PermissionPolicyHash = null);
