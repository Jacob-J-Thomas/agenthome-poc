using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Loops;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Persists bounded immutable content-addressed workspace before, after, outcome, and tombstone evidence.</summary>
public sealed class WorkspaceActionEvidenceStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };
    private readonly string _afterRoot;
    private readonly string _beforeRoot;
    private readonly WorkspaceActionPrivateArtifactPathGuard _guard;
    private readonly string _outcomeRoot;
    private readonly WorkspaceActionStorageLimits _quota;
    private readonly string _tombstoneRoot;

    /// <summary>Creates one workspace-scoped immutable evidence store.</summary>
    public WorkspaceActionEvidenceStore(WorkspacePaths paths, WorkspaceActionStorageLimits? quota = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _quota = WorkspaceActionStorageLimits.Validate(quota);
        var root = Path.Combine(paths.AgentPath, "loops", "execution", "workspace-actions");
        _beforeRoot = Path.Combine(root, "before");
        _afterRoot = Path.Combine(root, "after");
        _outcomeRoot = Path.Combine(root, "outcomes");
        _tombstoneRoot = Path.Combine(root, "tombstones");
        _guard = new WorkspaceActionPrivateArtifactPathGuard(paths.RootPath);
    }

    /// <summary>Retains or exactly replays one immutable before-evidence record.</summary>
    public Task RetainBeforeAsync(WorkspaceActionBeforeEvidence evidence, CancellationToken cancellationToken = default)
        => RetainAsync(_beforeRoot, evidence.EvidenceId, evidence, WorkspaceActionEvidenceContract.ValidateBefore, cancellationToken);

    /// <summary>Reads and authenticates one immutable before-evidence record by its content-addressed identifier.</summary>
    public Task<WorkspaceActionBeforeEvidence?> ReadBeforeAsync(string evidenceId, CancellationToken cancellationToken = default)
        => IsContentAddressedIdentifier(evidenceId, "before-")
            ? ReadAsync<WorkspaceActionBeforeEvidence>(_beforeRoot, evidenceId, WorkspaceActionEvidenceContract.ValidateBefore, cancellationToken)
            : Task.FromResult<WorkspaceActionBeforeEvidence?>(null);

    internal async Task<IReadOnlyList<WorkspaceActionBeforeEvidence>> ReadPreparationCleanupCandidatesAsync(
        DateTimeOffset capturedBeforeOrAtUtc,
        int maximumCount,
        ulong cursor,
        CancellationToken cancellationToken = default)
    {
        if (capturedBeforeOrAtUtc == default
            || capturedBeforeOrAtUtc.Offset != TimeSpan.Zero
            || maximumCount is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCount), "Preparation cleanup requires bounded count and canonical UTC cutoff evidence.");
        }
        _guard.PrepareRoot(_beforeRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_beforeRoot, cancellationToken).ConfigureAwait(false);
        var names = EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var examinationCount = Math.Min(maximumCount, names.Length);
        var start = names.Length == 0 ? 0 : (int)(cursor % (ulong)names.Length);
        var candidates = new List<WorkspaceActionBeforeEvidence>(examinationCount);
        for (var index = 0; index < examinationCount; index++)
        {
            var name = names[(start + index) % names.Length];
            var candidate = await ReadPathAsync<WorkspaceActionBeforeEvidence>(
                ownership,
                name,
                WorkspaceActionEvidenceContract.ValidateBefore,
                cancellationToken).ConfigureAwait(false);
            if (candidate.CapturedAtUtc <= capturedBeforeOrAtUtc)
            {
                candidates.Add(candidate);
            }
        }
        return Array.AsReadOnly(candidates
            .OrderBy(candidate => candidate.CapturedAtUtc)
            .ThenBy(candidate => candidate.EvidenceId, StringComparer.Ordinal)
            .Take(maximumCount)
            .ToArray());
    }

    internal async Task<bool> DeleteExactBeforeAsync(
        WorkspaceActionBeforeEvidence expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (WorkspaceActionEvidenceContract.ValidateBefore(expected) is not null)
        {
            return false;
        }
        _guard.PrepareRoot(_beforeRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_beforeRoot, cancellationToken).ConfigureAwait(false);
        _ = EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind);
        var fileName = expected.EvidenceId + ".json";
        if (!_guard.FileExists(ownership, fileName))
        {
            return false;
        }
        var retained = await ReadPathAsync<WorkspaceActionBeforeEvidence>(
            ownership,
            fileName,
            WorkspaceActionEvidenceContract.ValidateBefore,
            cancellationToken).ConfigureAwait(false);
        if (!Equals(retained, expected))
        {
            return false;
        }
        using var file = WorkspaceActionNativeFileSystem.OpenRelativeFile(ownership.DirectoryHandle, fileName, allowMissing: false, write: true)!;
        var identity = WorkspaceActionNativeFileSystem.GetIdentity(file);
        WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, fileName, file, identity);
        WorkspaceActionNativeFileSystem.FlushDirectory(ownership.DirectoryHandle);
        return true;
    }

    /// <summary>Finds at most one retained before record for the exact same physical and optimistic state.</summary>
    public async Task<WorkspaceActionBeforeEvidence?> FindBeforeStateAsync(
        string scopeId,
        string targetReference,
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
        CancellationToken cancellationToken = default)
    {
        _guard.PrepareRoot(_beforeRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_beforeRoot, cancellationToken).ConfigureAwait(false);
        WorkspaceActionBeforeEvidence? match = null;
        foreach (var name in EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind))
        {
            var candidate = await ReadPathAsync<WorkspaceActionBeforeEvidence>(
                ownership,
                name,
                WorkspaceActionEvidenceContract.ValidateBefore,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(candidate.ScopeId, scopeId, StringComparison.Ordinal)
                && string.Equals(candidate.TargetReference, targetReference, StringComparison.Ordinal)
                && string.Equals(candidate.TargetFingerprint, targetFingerprint, StringComparison.Ordinal)
                && string.Equals(candidate.PreconditionEvidenceHash, preconditionEvidenceHash, StringComparison.Ordinal)
                && candidate.EntryKind == entryKind
                && candidate.PermissionOperation == permissionOperation
                && string.Equals(candidate.PermissionPolicyHash, permissionPolicyHash, StringComparison.Ordinal)
                && string.Equals(candidate.RootIdentityFingerprint, rootIdentityFingerprint, StringComparison.Ordinal)
                && string.Equals(candidate.ParentIdentityFingerprint, parentIdentityFingerprint, StringComparison.Ordinal)
                && string.Equals(candidate.NativeIdentityFingerprint, nativeIdentityFingerprint, StringComparison.Ordinal)
                && string.Equals(candidate.ContentHash, contentHash, StringComparison.Ordinal)
                && candidate.ByteCount == byteCount
                && candidate.GovernedVersion == governedVersion)
            {
                if (match is not null)
                {
                    throw new FormatException("Workspace action evidence contains duplicate before records for one exact physical state.");
                }
                match = candidate;
            }
        }
        return match;
    }

    /// <summary>Proves that retained evidence has never admitted a distinct textual alias for one native target fingerprint.</summary>
    public async Task<bool> IsUniqueTargetReferenceAsync(
        string targetFingerprint,
        string targetReference,
        string rootIdentityFingerprint,
        string parentIdentityFingerprint,
        string? nativeIdentityFingerprint,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActionFingerprint.IsCanonicalSha256(targetFingerprint)
            || !WorkspaceRelativeFileTarget.TryParse(targetReference, out _, out _)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(rootIdentityFingerprint)
            || !WorkspaceActionFingerprint.IsCanonicalSha256(parentIdentityFingerprint)
            || nativeIdentityFingerprint is not null && !WorkspaceActionFingerprint.IsCanonicalSha256(nativeIdentityFingerprint))
        {
            return false;
        }
        _guard.PrepareRoot(_beforeRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_beforeRoot, cancellationToken).ConfigureAwait(false);
        foreach (var name in EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind))
        {
            var candidate = await ReadPathAsync<WorkspaceActionBeforeEvidence>(
                ownership,
                name,
                WorkspaceActionEvidenceContract.ValidateBefore,
                cancellationToken).ConfigureAwait(false);
            var sameResolvedTarget = string.Equals(candidate.TargetFingerprint, targetFingerprint, StringComparison.Ordinal)
                || nativeIdentityFingerprint is not null
                    && string.Equals(candidate.RootIdentityFingerprint, rootIdentityFingerprint, StringComparison.Ordinal)
                    && string.Equals(candidate.ParentIdentityFingerprint, parentIdentityFingerprint, StringComparison.Ordinal)
                    && string.Equals(candidate.NativeIdentityFingerprint, nativeIdentityFingerprint, StringComparison.Ordinal);
            if (sameResolvedTarget
                && !string.Equals(candidate.TargetReference, targetReference, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Retains or exactly replays one immutable after-evidence record.</summary>
    public Task RetainAfterAsync(WorkspaceActionAfterEvidence evidence, CancellationToken cancellationToken = default)
        => RetainAsync(_afterRoot, evidence.EvidenceId, evidence, WorkspaceActionEvidenceContract.ValidateAfter, cancellationToken);

    /// <summary>Reads and authenticates one immutable after-evidence record by its content-addressed identifier.</summary>
    public Task<WorkspaceActionAfterEvidence?> ReadAfterAsync(string evidenceId, CancellationToken cancellationToken = default)
        => IsContentAddressedIdentifier(evidenceId, "after-")
            ? ReadAsync<WorkspaceActionAfterEvidence>(_afterRoot, evidenceId, WorkspaceActionEvidenceContract.ValidateAfter, cancellationToken)
            : Task.FromResult<WorkspaceActionAfterEvidence?>(null);

    /// <summary>Retains or exactly replays one immutable outcome-evidence record.</summary>
    public Task RetainOutcomeAsync(WorkspaceActionOutcomeEvidence evidence, CancellationToken cancellationToken = default)
        => RetainAsync(_outcomeRoot, evidence.EvidenceId, evidence, WorkspaceActionEvidenceContract.ValidateOutcome, cancellationToken);

    /// <summary>Reads and authenticates one immutable outcome-evidence record by its content-addressed identifier.</summary>
    public Task<WorkspaceActionOutcomeEvidence?> ReadOutcomeAsync(string evidenceId, CancellationToken cancellationToken = default)
        => IsContentAddressedIdentifier(evidenceId, "outcome-")
            ? ReadAsync<WorkspaceActionOutcomeEvidence>(_outcomeRoot, evidenceId, WorkspaceActionEvidenceContract.ValidateOutcome, cancellationToken)
            : Task.FromResult<WorkspaceActionOutcomeEvidence?>(null);

    /// <summary>Retains or exactly replays one immutable recoverable-delete tombstone.</summary>
    public Task RetainTombstoneAsync(WorkspaceActionTombstone tombstone, CancellationToken cancellationToken = default)
        => RetainAsync(_tombstoneRoot, tombstone.TombstoneReference, tombstone, WorkspaceActionEvidenceContract.ValidateTombstone, cancellationToken, _quota.MaximumTombstones);

    /// <summary>Reads and authenticates one immutable tombstone by its content-addressed reference.</summary>
    public Task<WorkspaceActionTombstone?> ReadTombstoneAsync(string tombstoneReference, CancellationToken cancellationToken = default)
        => IsContentAddressedIdentifier(tombstoneReference, "tombstone-")
            ? ReadAsync<WorkspaceActionTombstone>(_tombstoneRoot, tombstoneReference, WorkspaceActionEvidenceContract.ValidateTombstone, cancellationToken)
            : Task.FromResult<WorkspaceActionTombstone?>(null);

    internal async Task<WorkspaceActionTombstone?> FindTombstoneAsync(
        string beforeEvidenceId,
        string quarantineReference,
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        CancellationToken cancellationToken = default)
    {
        _guard.PrepareRoot(_tombstoneRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_tombstoneRoot, cancellationToken).ConfigureAwait(false);
        WorkspaceActionTombstone? match = null;
        foreach (var name in EvidenceNames(ownership, _quota.MaximumTombstones))
        {
            var candidate = await ReadPathAsync<WorkspaceActionTombstone>(
                ownership,
                name,
                WorkspaceActionEvidenceContract.ValidateTombstone,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(candidate.BeforeEvidenceId, beforeEvidenceId, StringComparison.Ordinal)
                && string.Equals(candidate.QuarantineReference, quarantineReference, StringComparison.Ordinal)
                && string.Equals(candidate.EffectId, effectId, StringComparison.Ordinal)
                && string.Equals(candidate.IdempotencyOperationId, idempotencyOperationId, StringComparison.Ordinal)
                && candidate.EffectGeneration == effectGeneration)
            {
                if (match is not null)
                {
                    throw new FormatException("Workspace action evidence contains conflicting tombstones for one delete attempt.");
                }
                match = candidate;
            }
        }
        return match;
    }

    internal async Task<bool> DeleteExactTombstoneAsync(WorkspaceActionTombstone expected, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (WorkspaceActionEvidenceContract.ValidateTombstone(expected) is not null)
        {
            return false;
        }
        _guard.PrepareRoot(_tombstoneRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_tombstoneRoot, cancellationToken).ConfigureAwait(false);
        _ = EvidenceNames(ownership, _quota.MaximumTombstones);
        var fileName = expected.TombstoneReference + ".json";
        if (!_guard.FileExists(ownership, fileName))
        {
            return false;
        }
        var retained = await ReadPathAsync<WorkspaceActionTombstone>(
            ownership,
            fileName,
            WorkspaceActionEvidenceContract.ValidateTombstone,
            cancellationToken).ConfigureAwait(false);
        if (!Equals(retained, expected))
        {
            return false;
        }
        using var file = WorkspaceActionNativeFileSystem.OpenRelativeFile(ownership.DirectoryHandle, fileName, allowMissing: false, write: true)!;
        var identity = WorkspaceActionNativeFileSystem.GetIdentity(file);
        WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, fileName, file, identity);
        WorkspaceActionNativeFileSystem.FlushDirectory(ownership.DirectoryHandle);
        return true;
    }

    /// <summary>Finds at most one authenticated after record for the exact stable effect generation.</summary>
    public async Task<WorkspaceActionAfterEvidence?> FindAfterAsync(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(effectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(idempotencyOperationId)
            || effectGeneration < 1)
        {
            return null;
        }
        _guard.PrepareRoot(_afterRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_afterRoot, cancellationToken).ConfigureAwait(false);
        var matches = new List<WorkspaceActionAfterEvidence>(2);
        foreach (var name in EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind))
        {
            var candidate = await ReadPathAsync<WorkspaceActionAfterEvidence>(
                ownership,
                name,
                WorkspaceActionEvidenceContract.ValidateAfter,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(candidate.EffectId, effectId, StringComparison.Ordinal)
                && string.Equals(candidate.IdempotencyOperationId, idempotencyOperationId, StringComparison.Ordinal)
                && candidate.EffectGeneration == effectGeneration)
            {
                matches.Add(candidate);
                if (matches.Count > 1)
                {
                    throw new FormatException("Workspace action evidence contains conflicting after records for one effect generation.");
                }
            }
        }
        return matches.SingleOrDefault();
    }

    /// <summary>Finds at most one authenticated outcome record for the exact stable effect generation.</summary>
    public async Task<WorkspaceActionOutcomeEvidence?> FindOutcomeAsync(
        string effectId,
        string idempotencyOperationId,
        long effectGeneration,
        CancellationToken cancellationToken = default)
    {
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(effectId)
            || !WorkspaceActionFingerprint.IsEvidenceIdentifier(idempotencyOperationId)
            || effectGeneration < 1)
        {
            return null;
        }
        _guard.PrepareRoot(_outcomeRoot);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(_outcomeRoot, cancellationToken).ConfigureAwait(false);
        var matches = new List<WorkspaceActionOutcomeEvidence>(2);
        foreach (var name in EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind))
        {
            var candidate = await ReadPathAsync<WorkspaceActionOutcomeEvidence>(
                ownership,
                name,
                WorkspaceActionEvidenceContract.ValidateOutcome,
                cancellationToken).ConfigureAwait(false);
            if (string.Equals(candidate.EffectId, effectId, StringComparison.Ordinal)
                && string.Equals(candidate.IdempotencyOperationId, idempotencyOperationId, StringComparison.Ordinal)
                && candidate.EffectGeneration == effectGeneration)
            {
                matches.Add(candidate);
                if (matches.Count > 1)
                {
                    throw new FormatException("Workspace action evidence contains conflicting outcome records for one effect generation.");
                }
            }
        }
        return matches.SingleOrDefault();
    }

    private async Task RetainAsync<T>(
        string root,
        string identifier,
        T evidence,
        Func<T?, string?> validate,
        CancellationToken cancellationToken,
        int? maximumRecords = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var reasonCode = validate(evidence);
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(identifier) || reasonCode is not null)
        {
            throw new ArgumentException(reasonCode ?? "Workspace evidence identifier is invalid.", nameof(evidence));
        }
        var encoded = JsonSerializer.Serialize(evidence, _jsonOptions);
        var byteCount = Encoding.UTF8.GetByteCount(encoded);
        if (byteCount > WorkspaceActionContractLimits.MaxEvidenceUtf8Bytes)
        {
            throw new InvalidOperationException("Workspace action evidence exceeds its immutable record byte bound.");
        }

        _guard.PrepareRoot(root);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(root, cancellationToken).ConfigureAwait(false);
        var effectiveMaximumRecords = maximumRecords ?? _quota.MaximumEvidenceRecordsPerKind;
        var names = EvidenceNames(ownership, effectiveMaximumRecords);
        var fileName = identifier + ".json";
        if (!names.Contains(fileName, StringComparer.Ordinal) && names.Count >= effectiveMaximumRecords)
        {
            throw new WorkspaceActionEvidenceCapacityException();
        }
        if (!await _guard.WriteTextAtomicallyIfAbsentAsync(ownership, fileName, encoded, cancellationToken).ConfigureAwait(false))
        {
            var retained = await ReadPathAsync<T>(ownership, fileName, validate, cancellationToken).ConfigureAwait(false);
            var retainedEncoded = JsonSerializer.Serialize(retained, _jsonOptions);
            if (!string.Equals(retainedEncoded, encoded, StringComparison.Ordinal))
            {
                throw new FormatException("Immutable workspace action evidence conflicts with the retained content-addressed record.");
            }
        }
    }

    private async Task<T?> ReadAsync<T>(
        string root,
        string identifier,
        Func<T?, string?> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        if (!WorkspaceActionFingerprint.IsEvidenceIdentifier(identifier))
        {
            return null;
        }
        _guard.PrepareRoot(root);
        using var ownership = await _guard.AcquireExclusiveReadLockAsync(root, cancellationToken).ConfigureAwait(false);
        _ = EvidenceNames(ownership, _quota.MaximumEvidenceRecordsPerKind);
        var fileName = identifier + ".json";
        return _guard.FileExists(ownership, fileName)
            ? await ReadPathAsync(ownership, fileName, validate, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<T> ReadPathAsync<T>(
        WorkspaceActionPrivateArtifactLockLease ownership,
        string fileName,
        Func<T?, string?> validate,
        CancellationToken cancellationToken)
        where T : class
    {
        var bytes = await _guard.ReadAllBytesAsync(
            ownership,
            fileName,
            WorkspaceActionContractLimits.MaxEvidenceUtf8Bytes,
            "Workspace action evidence",
            cancellationToken).ConfigureAwait(false);
        T? evidence;
        try
        {
            evidence = JsonSerializer.Deserialize<T>(bytes, _jsonOptions);
        }
        catch (JsonException exception)
        {
            throw new FormatException("Workspace action evidence is malformed.", exception);
        }
        if (validate(evidence) is { } reasonCode)
        {
            throw new FormatException($"Workspace action evidence is not authentic: {reasonCode}.");
        }
        return evidence!;
    }

    private IReadOnlyList<string> EvidenceNames(
        WorkspaceActionPrivateArtifactLockLease ownership,
        int maximumRecords)
    {
        var maximumEntries = checked(maximumRecords * 2 + 1);
        var names = _guard.EnumerateNames(ownership, maximumEntries + 1).ToArray();
        if (names.Length > maximumEntries)
        {
            throw new FormatException("Workspace action evidence exceeds its finite record bound.");
        }
        var records = new List<string>(names.Length);
        var temporaries = new List<string>();
        foreach (var name in names)
        {
            if (string.Equals(name, ".custom-loop-mutations.lock", StringComparison.Ordinal))
            {
                continue;
            }
            if (IsAtomicTemporaryName(name))
            {
                temporaries.Add(name);
                continue;
            }
            if (!name.EndsWith(".json", StringComparison.Ordinal)
                || !WorkspaceActionFingerprint.IsEvidenceIdentifier(name[..^5]))
            {
                throw new FormatException("Workspace action evidence contains an unsupported artifact.");
            }
            using var file = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                ownership.DirectoryHandle,
                name,
                allowMissing: false,
                write: false)!;
            records.Add(name);
        }
        if (records.Count > maximumRecords || temporaries.Count > maximumRecords)
        {
            throw new FormatException("Workspace action evidence exceeds its finite record or crash-recovery bound.");
        }
        if (temporaries.Count > 0)
        {
            foreach (var name in temporaries)
            {
                using var temporary = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                    ownership.DirectoryHandle,
                    name,
                    allowMissing: false,
                    write: true)!;
                var identity = WorkspaceActionNativeFileSystem.GetIdentity(temporary);
                WorkspaceActionNativeFileSystem.DeleteExact(ownership.DirectoryHandle, name, temporary, identity);
            }
            WorkspaceActionNativeFileSystem.FlushDirectory(ownership.DirectoryHandle);
        }
        return records;
    }

    private static bool IsAtomicTemporaryName(string name)
    {
        if (name.Length < 1 || name[0] != '.'
            || !name.EndsWith(".tmp", StringComparison.Ordinal))
        {
            return false;
        }
        var withoutSuffix = name.AsSpan(1, name.Length - ".tmp".Length - 1);
        var separator = withoutSuffix.LastIndexOf('.');
        if (separator < 1)
        {
            return false;
        }
        var destinationName = withoutSuffix[..separator];
        var nonce = withoutSuffix[(separator + 1)..];
        return destinationName.EndsWith(".json", StringComparison.Ordinal)
            && WorkspaceActionFingerprint.IsEvidenceIdentifier(destinationName[..^5].ToString())
            && nonce.Length == 32
            && IsLowerHex(nonce);
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsContentAddressedIdentifier(string? value, string prefix)
        => value is not null
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && WorkspaceActionFingerprint.IsCanonicalSha256(value[prefix.Length..]);
}
