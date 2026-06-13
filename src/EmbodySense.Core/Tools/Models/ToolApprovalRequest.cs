using EmbodySense.Core.Permissions.Models;

namespace EmbodySense.Core.Tools.Models;

public sealed record ToolApprovalRequest(
    string RequestId,
    ToolRequest ToolRequest,
    string ResolvedPath,
    FileSystemOperation Operation,
    PermissionEvaluation PermissionEvaluation);
