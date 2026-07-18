using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

public sealed record ToolApprovalRequest(
    string RequestId,
    ToolRequest ToolRequest,
    string ResolvedPath,
    FileSystemOperation Operation,
    PermissionEvaluation PermissionEvaluation,
    string? PermissionPolicyHash = null);
