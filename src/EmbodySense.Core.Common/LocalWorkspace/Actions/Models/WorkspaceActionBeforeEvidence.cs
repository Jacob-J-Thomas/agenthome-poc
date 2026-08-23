using EmbodySense.Core.Common.Governance.Permissions.Models;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

/// <summary>Contains immutable, bounded, value-free evidence of the exact target immediately before intent.</summary>
public sealed record WorkspaceActionBeforeEvidence(
    int SchemaVersion,
    string EvidenceId,
    string ScopeId,
    string TargetReference,
    string TargetFingerprint,
    string PreconditionEvidenceHash,
    WorkspaceActionEntryKind EntryKind,
    FileSystemOperation PermissionOperation,
    string PermissionPolicyHash,
    string RootIdentityFingerprint,
    string ParentIdentityFingerprint,
    string? NativeIdentityFingerprint,
    string? ContentHash,
    long ByteCount,
    long GovernedVersion,
    DateTimeOffset CapturedAtUtc,
    string ContentHashOfRecord);
