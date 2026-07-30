using EmbodySense.Core.Common.Governance.Permissions;
using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.Governance.Tools.Models;

/// <summary>
/// Represents a tool permission check.
/// </summary>
/// <param name="ResolvedPath">The resolved path.</param>
/// <param name="PermissionTargetPath">The permission target path.</param>
/// <param name="Operation">The operation.</param>
/// <param name="Evaluation">The evaluation.</param>
/// <param name="PolicyHash">The policy hash.</param>
public sealed record ToolPermissionCheck(
    string ResolvedPath,
    string PermissionTargetPath,
    FileSystemOperation Operation,
    PermissionEvaluation Evaluation,
    string PolicyHash);
