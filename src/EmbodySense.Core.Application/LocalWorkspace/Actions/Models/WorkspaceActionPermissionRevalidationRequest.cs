using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Application.LocalWorkspace.Actions.Models;

/// <summary>Identifies one exact server-derived permission class and retained physical target state.</summary>
public sealed record WorkspaceActionPermissionRevalidationRequest(
    WorkspaceActionInput Input,
    WorkspaceActionEntryKind EntryKind,
    FileSystemOperation Operation,
    string TargetFingerprint,
    string RootIdentityFingerprint,
    string ParentIdentityFingerprint,
    string? NativeIdentityFingerprint);
