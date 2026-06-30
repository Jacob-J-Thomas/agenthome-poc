using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

public sealed record ToolPermissionCheck(
    string ResolvedPath,
    string PermissionTargetPath,
    FileSystemOperation Operation,
    PermissionEvaluation Evaluation);
