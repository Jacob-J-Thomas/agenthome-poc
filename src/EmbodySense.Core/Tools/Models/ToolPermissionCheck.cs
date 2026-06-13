using EmbodySense.Core.Permissions.Models;

namespace EmbodySense.Core.Tools.Models;

public sealed record ToolPermissionCheck(
    string ResolvedPath,
    string PermissionTargetPath,
    FileSystemOperation Operation,
    PermissionEvaluation Evaluation);
