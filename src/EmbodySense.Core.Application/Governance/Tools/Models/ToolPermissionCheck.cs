using EmbodySense.Core.Application.Governance.Permissions.Models;

namespace EmbodySense.Core.Application.Governance.Tools.Models;

public sealed record ToolPermissionCheck(
    string ResolvedPath,
    string PermissionTargetPath,
    FileSystemOperation Operation,
    PermissionEvaluation Evaluation);
