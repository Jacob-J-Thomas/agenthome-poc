using System.Globalization;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;

namespace EmbodySense.Core.Common.LocalWorkspace.Actions;

/// <summary>Creates and validates immutable value-free workspace before, after, outcome, and recoverable-delete evidence.</summary>
public static class WorkspaceActionEvidenceContract
{
    /// <summary>Creates one exact before-state record whose opaque identifier authenticates the canonical record hash.</summary>
    public static WorkspaceActionBeforeEvidence CreateBefore(
        WorkspaceActionScopeId scopeId,
        WorkspaceRelativeFileTarget target,
        string targetFingerprint,
        string preconditionEvidenceHash,
        WorkspaceActionEntryKind entryKind,
        FileSystemOperation permissionOperation,
        string permissionPolicyHash,
        string rootIdentityFingerprint,
        string parentIdentityFingerprint,
        string? nativeIdentityFingerprint,
        string? contentHash,
        long byteCount,
        long governedVersion,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(scopeId);
        ArgumentNullException.ThrowIfNull(target);
        var candidate = new WorkspaceActionBeforeEvidence(
            WorkspaceActionContractLimits.CurrentSchemaVersion,
            string.Empty,
            scopeId.Value,
            target.Value,
            targetFingerprint,
            preconditionEvidenceHash,
            entryKind,
            permissionOperation,
            permissionPolicyHash,
            rootIdentityFingerprint,
            parentIdentityFingerprint,
            nativeIdentityFingerprint,
            contentHash,
            byteCount,
            governedVersion,
            capturedAtUtc,
            string.Empty);
        var error = ValidateBeforeCore(candidate, requireHashes: false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(target));
        }
        var hash = ComputeBefore(candidate);
        return candidate with { EvidenceId = "before-" + hash, ContentHashOfRecord = hash };
    }

    /// <summary>Returns a bounded reason code when before evidence is invalid; otherwise <see langword="null"/>.</summary>
    public static string? ValidateBefore(WorkspaceActionBeforeEvidence? evidence) => ValidateBeforeCore(evidence, requireHashes: true);

    /// <summary>Creates one exact after-state record whose opaque identifier authenticates the canonical record hash.</summary>
    public static WorkspaceActionAfterEvidence CreateAfter(
        WorkspaceActionBeforeEvidence before,
        string operationId,
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        WorkspaceActionEntryKind entryKind,
        string? nativeIdentityFingerprint,
        string? contentHash,
        long byteCount,
        long appendedByteCount,
        long governedVersion,
        string? quarantineReference,
        string? tombstoneReference,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(before);
        if (ValidateBefore(before) is { } beforeError)
        {
            throw new ArgumentException(beforeError, nameof(before));
        }
        var nextVersion = checked(before.GovernedVersion + 1);
        if (governedVersion != nextVersion || observedAtUtc < before.CapturedAtUtc)
        {
            throw new ArgumentException("Workspace after evidence must be the direct chronological successor of its exact before evidence.", nameof(governedVersion));
        }
        if (operationId == WorkspaceActionOperationIds.Append
            && (appendedByteCount > byteCount
                || byteCount != checked(before.ByteCount + appendedByteCount))
            || operationId == WorkspaceActionOperationIds.Write && appendedByteCount != 0
            || operationId == WorkspaceActionOperationIds.Delete && before.EntryKind != WorkspaceActionEntryKind.RegularFile)
        {
            throw new ArgumentException("Workspace after evidence does not match the exact operation transition.", nameof(operationId));
        }
        var candidate = new WorkspaceActionAfterEvidence(
            WorkspaceActionContractLimits.CurrentSchemaVersion,
            string.Empty,
            before.EvidenceId,
            operationId,
            effectId,
            idempotencyOperationId,
            effectGeneration,
            before.ScopeId,
            before.TargetReference,
            before.TargetFingerprint,
            entryKind,
            nativeIdentityFingerprint,
            contentHash,
            byteCount,
            appendedByteCount,
            governedVersion,
            quarantineReference,
            tombstoneReference,
            observedAtUtc,
            string.Empty);
        var error = ValidateAfterCore(candidate, requireHashes: false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(operationId));
        }
        var hash = ComputeAfter(candidate);
        return candidate with { EvidenceId = "after-" + hash, ContentHashOfRecord = hash };
    }

    /// <summary>Returns a bounded reason code when after evidence is invalid; otherwise <see langword="null"/>.</summary>
    public static string? ValidateAfter(WorkspaceActionAfterEvidence? evidence) => ValidateAfterCore(evidence, requireHashes: true);

    /// <summary>Creates one distinct outcome record that authenticates an exact retained after-state.</summary>
    public static WorkspaceActionOutcomeEvidence CreateOutcome(WorkspaceActionAfterEvidence after)
    {
        ArgumentNullException.ThrowIfNull(after);
        if (ValidateAfter(after) is { } afterError)
        {
            throw new ArgumentException(afterError, nameof(after));
        }
        var candidate = new WorkspaceActionOutcomeEvidence(
            WorkspaceActionContractLimits.CurrentSchemaVersion,
            string.Empty,
            after.BeforeEvidenceId,
            after.EvidenceId,
            after.ContentHashOfRecord,
            after.OperationId,
            after.EffectId,
            after.IdempotencyOperationId,
            after.EffectGeneration,
            after.TargetFingerprint,
            after.GovernedVersion,
            after.TombstoneReference,
            after.ObservedAtUtc,
            string.Empty);
        var error = ValidateOutcomeCore(candidate, requireHashes: false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(after));
        }
        var hash = ComputeOutcome(candidate);
        return candidate with { EvidenceId = "outcome-" + hash, ContentHashOfRecord = hash };
    }

    /// <summary>Returns a bounded reason code when outcome evidence is invalid; otherwise <see langword="null"/>.</summary>
    public static string? ValidateOutcome(WorkspaceActionOutcomeEvidence? evidence) => ValidateOutcomeCore(evidence, requireHashes: true);

    /// <summary>Creates one immutable value-free recoverable-delete tombstone.</summary>
    public static WorkspaceActionTombstone CreateTombstone(
        WorkspaceActionBeforeEvidence before,
        string quarantineReference,
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        long governedVersion,
        DateTimeOffset quarantinedAtUtc,
        DateTimeOffset retainUntilUtc)
    {
        ArgumentNullException.ThrowIfNull(before);
        var beforeError = ValidateBefore(before);
        if (beforeError is not null
            || before.EntryKind != WorkspaceActionEntryKind.RegularFile
            || before.NativeIdentityFingerprint is null
            || before.ContentHash is null
            || governedVersion != checked(before.GovernedVersion + 1)
            || quarantinedAtUtc < before.CapturedAtUtc)
        {
            throw new ArgumentException(beforeError ?? "Delete before evidence must identify one regular file.", nameof(before));
        }
        var candidate = new WorkspaceActionTombstone(
            WorkspaceActionContractLimits.CurrentSchemaVersion,
            string.Empty,
            before.EvidenceId,
            before.ScopeId,
            before.TargetReference,
            before.TargetFingerprint,
            before.NativeIdentityFingerprint,
            before.ContentHash,
            before.ByteCount,
            quarantineReference,
            effectId,
            idempotencyOperationId,
            effectGeneration,
            governedVersion,
            quarantinedAtUtc,
            retainUntilUtc,
            string.Empty);
        var error = ValidateTombstoneCore(candidate, requireHashes: false);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(quarantineReference));
        }
        var hash = ComputeTombstone(candidate);
        return candidate with { TombstoneReference = "tombstone-" + hash, ContentHashOfRecord = hash };
    }

    /// <summary>Returns a bounded reason code when a tombstone is invalid; otherwise <see langword="null"/>.</summary>
    public static string? ValidateTombstone(WorkspaceActionTombstone? tombstone) => ValidateTombstoneCore(tombstone, requireHashes: true);

    private static string? ValidateBeforeCore(WorkspaceActionBeforeEvidence? evidence, bool requireHashes)
    {
        if (evidence is null || evidence.SchemaVersion != WorkspaceActionContractLimits.CurrentSchemaVersion)
        {
            return "workspace-before-schema-invalid";
        }
        if (!WorkspaceActionScopeId.TryParse(evidence.ScopeId, out _)
            || !WorkspaceRelativeFileTarget.TryParse(evidence.TargetReference, out _, out _)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.TargetFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.PreconditionEvidenceHash)
            || evidence.PermissionOperation is not (FileSystemOperation.Create or FileSystemOperation.Append or FileSystemOperation.Modify or FileSystemOperation.Delete)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.PermissionPolicyHash)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.RootIdentityFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.ParentIdentityFingerprint)
            || evidence.CapturedAtUtc == default
            || evidence.CapturedAtUtc.Offset != TimeSpan.Zero
            || evidence.ByteCount is < 0 or > WorkspaceActionContractLimits.MaxBeforeImageBytes
            || evidence.GovernedVersion < 0)
        {
            return "workspace-before-field-invalid";
        }
        if (evidence.EntryKind == WorkspaceActionEntryKind.Absent)
        {
            if (evidence.NativeIdentityFingerprint is not null || evidence.ContentHash is not null || evidence.ByteCount != 0)
            {
                return "workspace-before-absence-invalid";
            }
        }
        else if (evidence.EntryKind == WorkspaceActionEntryKind.RegularFile)
        {
            if (!WorkspaceActionFingerprint.IsCanonicalSha256(evidence.NativeIdentityFingerprint)
                || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.ContentHash))
            {
                return "workspace-before-file-invalid";
            }
        }
        else
        {
            return "workspace-before-kind-invalid";
        }
        if (!requireHashes)
        {
            return evidence.EvidenceId.Length == 0 && evidence.ContentHashOfRecord.Length == 0 ? null : "workspace-before-hash-premature";
        }
        var expectedHash = ComputeBefore(evidence);
        return string.Equals(evidence.EvidenceId, "before-" + expectedHash, StringComparison.Ordinal)
            && string.Equals(evidence.ContentHashOfRecord, expectedHash, StringComparison.Ordinal)
            ? null
            : "workspace-before-hash-mismatch";
    }

    private static string? ValidateAfterCore(WorkspaceActionAfterEvidence? evidence, bool requireHashes)
    {
        if (evidence is null || evidence.SchemaVersion != WorkspaceActionContractLimits.CurrentSchemaVersion)
        {
            return "workspace-after-schema-invalid";
        }
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(evidence.BeforeEvidenceId)
            || !WorkspaceActionOperationIds.TryParse(evidence.OperationId, out var operation)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(evidence.EffectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(evidence.IdempotencyOperationId)
            || evidence.EffectGeneration < 1
            || !WorkspaceActionScopeId.TryParse(evidence.ScopeId, out _)
            || !WorkspaceRelativeFileTarget.TryParse(evidence.TargetReference, out _, out _)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.TargetFingerprint)
            || evidence.ObservedAtUtc == default
            || evidence.ObservedAtUtc.Offset != TimeSpan.Zero
            || evidence.ByteCount is < 0 or > WorkspaceActionContractLimits.MaxAfterImageBytes
            || evidence.AppendedByteCount is < 0 or > WorkspaceActionContractLimits.MaxLiteralUtf8Bytes
            || evidence.GovernedVersion < 1)
        {
            return "workspace-after-field-invalid";
        }
        if (operation == Models.WorkspaceActionKind.Delete)
        {
            if (evidence.EntryKind != WorkspaceActionEntryKind.Absent
                || evidence.NativeIdentityFingerprint is not null
                || evidence.ContentHash is not null
                || evidence.ByteCount != 0
                || evidence.AppendedByteCount != 0
                || !IsContentAddressedReference(evidence.QuarantineReference, "quarantine-")
                || !IsContentAddressedReference(evidence.TombstoneReference, "tombstone-"))
            {
                return "workspace-after-delete-invalid";
            }
        }
        else if (evidence.EntryKind != WorkspaceActionEntryKind.RegularFile
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.NativeIdentityFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.ContentHash)
            || operation == Models.WorkspaceActionKind.Write && evidence.AppendedByteCount != 0
            || operation == Models.WorkspaceActionKind.Append && evidence.AppendedByteCount > evidence.ByteCount
            || evidence.QuarantineReference is not null
            || evidence.TombstoneReference is not null)
        {
            return "workspace-after-file-invalid";
        }
        if (!requireHashes)
        {
            return evidence.EvidenceId.Length == 0 && evidence.ContentHashOfRecord.Length == 0 ? null : "workspace-after-hash-premature";
        }
        var expectedHash = ComputeAfter(evidence);
        return string.Equals(evidence.EvidenceId, "after-" + expectedHash, StringComparison.Ordinal)
            && string.Equals(evidence.ContentHashOfRecord, expectedHash, StringComparison.Ordinal)
            ? null
            : "workspace-after-hash-mismatch";
    }

    private static string? ValidateTombstoneCore(WorkspaceActionTombstone? tombstone, bool requireHashes)
    {
        if (tombstone is null || tombstone.SchemaVersion != WorkspaceActionContractLimits.CurrentSchemaVersion)
        {
            return "workspace-tombstone-schema-invalid";
        }
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(tombstone.BeforeEvidenceId)
            || !WorkspaceActionScopeId.TryParse(tombstone.ScopeId, out _)
            || !WorkspaceRelativeFileTarget.TryParse(tombstone.TargetReference, out _, out _)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(tombstone.TargetFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(tombstone.NativeIdentityFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(tombstone.ContentHash)
            || tombstone.ByteCount is < 0 or > WorkspaceActionContractLimits.MaxBeforeImageBytes
            || !IsContentAddressedReference(tombstone.QuarantineReference, "quarantine-")
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(tombstone.EffectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(tombstone.IdempotencyOperationId)
            || tombstone.EffectGeneration < 1
            || tombstone.GovernedVersion < 1
            || tombstone.QuarantinedAtUtc == default
            || tombstone.QuarantinedAtUtc.Offset != TimeSpan.Zero
            || tombstone.RetainUntilUtc <= tombstone.QuarantinedAtUtc
            || tombstone.RetainUntilUtc.Offset != TimeSpan.Zero)
        {
            return "workspace-tombstone-field-invalid";
        }
        if (!requireHashes)
        {
            return tombstone.TombstoneReference.Length == 0 && tombstone.ContentHashOfRecord.Length == 0 ? null : "workspace-tombstone-hash-premature";
        }
        var expectedHash = ComputeTombstone(tombstone);
        return string.Equals(tombstone.TombstoneReference, "tombstone-" + expectedHash, StringComparison.Ordinal)
            && string.Equals(tombstone.ContentHashOfRecord, expectedHash, StringComparison.Ordinal)
            ? null
            : "workspace-tombstone-hash-mismatch";
    }

    private static string? ValidateOutcomeCore(WorkspaceActionOutcomeEvidence? evidence, bool requireHashes)
    {
        if (evidence is null || evidence.SchemaVersion != WorkspaceActionContractLimits.CurrentSchemaVersion)
        {
            return "workspace-outcome-schema-invalid";
        }
        if (!IsContentAddressedReference(evidence.BeforeEvidenceId, "before-")
            || !IsContentAddressedReference(evidence.AfterEvidenceId, "after-")
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.AfterEvidenceHash)
            || !WorkspaceActionOperationIds.TryParse(evidence.OperationId, out var operation)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(evidence.EffectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(evidence.IdempotencyOperationId)
            || evidence.EffectGeneration < 1
            || !WorkspaceActionFingerprint.IsCanonicalSha256(evidence.TargetFingerprint)
            || evidence.GovernedVersion < 1
            || evidence.ObservedAtUtc == default
            || evidence.ObservedAtUtc.Offset != TimeSpan.Zero)
        {
            return "workspace-outcome-field-invalid";
        }
        if (operation == Models.WorkspaceActionKind.Delete)
        {
            if (!IsContentAddressedReference(evidence.TombstoneReference, "tombstone-"))
            {
                return "workspace-outcome-delete-invalid";
            }
        }
        else if (evidence.TombstoneReference is not null)
        {
            return "workspace-outcome-file-invalid";
        }
        if (!requireHashes)
        {
            return evidence.EvidenceId.Length == 0 && evidence.ContentHashOfRecord.Length == 0 ? null : "workspace-outcome-hash-premature";
        }
        var expectedHash = ComputeOutcome(evidence);
        return string.Equals(evidence.EvidenceId, "outcome-" + expectedHash, StringComparison.Ordinal)
            && string.Equals(evidence.ContentHashOfRecord, expectedHash, StringComparison.Ordinal)
            ? null
            : "workspace-outcome-hash-mismatch";
    }

    private static string ComputeBefore(WorkspaceActionBeforeEvidence evidence)
        => WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-before-evidence.v1",
            evidence.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            evidence.ScopeId,
            evidence.TargetReference,
            evidence.TargetFingerprint,
            evidence.PreconditionEvidenceHash,
            ((int)evidence.EntryKind).ToString(CultureInfo.InvariantCulture),
            ((int)evidence.PermissionOperation).ToString(CultureInfo.InvariantCulture),
            evidence.PermissionPolicyHash,
            evidence.RootIdentityFingerprint,
            evidence.ParentIdentityFingerprint,
            evidence.NativeIdentityFingerprint,
            evidence.ContentHash,
            evidence.ByteCount.ToString(CultureInfo.InvariantCulture),
            evidence.GovernedVersion.ToString(CultureInfo.InvariantCulture),
            evidence.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static string ComputeAfter(WorkspaceActionAfterEvidence evidence)
        => WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-after-evidence.v1",
            evidence.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            evidence.BeforeEvidenceId,
            evidence.OperationId,
            evidence.EffectId,
            evidence.IdempotencyOperationId,
            evidence.EffectGeneration.ToString(CultureInfo.InvariantCulture),
            evidence.ScopeId,
            evidence.TargetReference,
            evidence.TargetFingerprint,
            ((int)evidence.EntryKind).ToString(CultureInfo.InvariantCulture),
            evidence.NativeIdentityFingerprint,
            evidence.ContentHash,
            evidence.ByteCount.ToString(CultureInfo.InvariantCulture),
            evidence.AppendedByteCount.ToString(CultureInfo.InvariantCulture),
            evidence.GovernedVersion.ToString(CultureInfo.InvariantCulture),
            evidence.QuarantineReference,
            evidence.TombstoneReference,
            evidence.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static string ComputeTombstone(WorkspaceActionTombstone tombstone)
        => WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-delete-tombstone.v1",
            tombstone.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            tombstone.BeforeEvidenceId,
            tombstone.ScopeId,
            tombstone.TargetReference,
            tombstone.TargetFingerprint,
            tombstone.NativeIdentityFingerprint,
            tombstone.ContentHash,
            tombstone.ByteCount.ToString(CultureInfo.InvariantCulture),
            tombstone.QuarantineReference,
            tombstone.EffectId,
            tombstone.IdempotencyOperationId,
            tombstone.EffectGeneration.ToString(CultureInfo.InvariantCulture),
            tombstone.GovernedVersion.ToString(CultureInfo.InvariantCulture),
            tombstone.QuarantinedAtUtc.ToString("O", CultureInfo.InvariantCulture),
            tombstone.RetainUntilUtc.ToString("O", CultureInfo.InvariantCulture));

    private static string ComputeOutcome(WorkspaceActionOutcomeEvidence evidence)
        => WorkspaceActionFingerprint.Compute(
            "embodysense.workspace-outcome-evidence.v1",
            evidence.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            evidence.BeforeEvidenceId,
            evidence.AfterEvidenceId,
            evidence.AfterEvidenceHash,
            evidence.OperationId,
            evidence.EffectId,
            evidence.IdempotencyOperationId,
            evidence.EffectGeneration.ToString(CultureInfo.InvariantCulture),
            evidence.TargetFingerprint,
            evidence.GovernedVersion.ToString(CultureInfo.InvariantCulture),
            evidence.TombstoneReference,
            evidence.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture));

    private static bool IsContentAddressedReference(string? value, string prefix)
        => value is not null
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && WorkspaceActionFingerprint.IsCanonicalSha256(value[prefix.Length..]);
}
