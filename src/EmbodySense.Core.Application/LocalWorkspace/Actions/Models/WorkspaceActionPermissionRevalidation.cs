using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Contains a bounded current-policy decision for one exact workspace mutation class.</summary>
public sealed record WorkspaceActionPermissionRevalidation(
    bool IsAllowed,
    FileSystemOperation Operation,
    string? PolicyHash);
