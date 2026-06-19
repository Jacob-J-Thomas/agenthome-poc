using EmbodySense.Core.Application.Governance.Permissions.Models;

namespace EmbodySense.Core.Application.Governance.Tools.Models;

public sealed record ToolApprovalRequest(
    string RequestId,
    ToolRequest ToolRequest,
    string ResolvedPath,
    FileSystemOperation Operation,
    PermissionEvaluation PermissionEvaluation);
