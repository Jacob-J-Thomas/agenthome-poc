using System.Security.Cryptography;
using EmbodySense.Core.Common.LocalWorkspace.Actions;
using EmbodySense.Core.Common.LocalWorkspace.Actions.Models;
using Microsoft.Win32.SafeHandles;

namespace EmbodySense.Core.Persistence.WorkspaceActions;

/// <summary>Owns retained no-follow root, ancestor, parent, and optional target handles for one exact relative target.</summary>
internal sealed class WorkspaceActionRetainedTargetSession : IDisposable
{
    private readonly List<(SafeFileHandle Handle, WorkspaceActionNativeFileStamp Identity, string? Name)> _directories = [];
    private readonly string _rootPath;
    private readonly WorkspaceRelativeFileTarget _target;
    private SafeFileHandle? _targetHandle;
    private readonly bool _allowMultipleLinksForProbe;

    private WorkspaceActionRetainedTargetSession(
        string rootPath,
        WorkspaceActionScopeId scopeId,
        WorkspaceRelativeFileTarget target,
        bool writeTarget,
        bool fenceTargetNamespace,
        bool fenceDirectoryNamespace,
        bool allowMultipleLinksForProbe)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _target = target;
        ScopeId = scopeId;
        _allowMultipleLinksForProbe = allowMultipleLinksForProbe;
        var root = WorkspaceActionNativeFileSystem.OpenAbsoluteDirectory(_rootPath, denyDeleteSharing: fenceDirectoryNamespace);
        try
        {
            var rootIdentity = WorkspaceActionNativeFileSystem.GetIdentity(root);
            _directories.Add((root, rootIdentity, null));
            using var privateDirectory = WorkspaceActionNativeFileSystem.OpenRelativeDirectory(root, ".agent");
            var privateIdentity = WorkspaceActionNativeFileSystem.GetIdentity(privateDirectory);
            if (!privateIdentity.SameMount(rootIdentity))
            {
                throw new IOException("The runtime-private workspace root crossed a filesystem mount or volume.");
            }
            var segments = target.Segments;
            var comparisonSegments = new string[segments.Count];
            var current = root;
            for (var index = 0; index < segments.Count - 1; index++)
            {
                comparisonSegments[index] = segments[index];
                var next = WorkspaceActionNativeFileSystem.OpenRelativeDirectory(current, segments[index], denyDeleteSharing: fenceDirectoryNamespace);
                try
                {
                    WorkspaceActionNativeFileSystem.RequireExactOpenedName(next, segments[index]);
                    var identity = WorkspaceActionNativeFileSystem.GetIdentity(next);
                    if (!identity.SameMount(rootIdentity))
                    {
                        throw new IOException("Workspace target traversal crossed a filesystem mount or volume.");
                    }
                    if (identity.SameEntry(privateIdentity))
                    {
                        throw new IOException("Workspace target traversal resolved into the runtime-private workspace root.");
                    }
                    _directories.Add((next, identity, segments[index]));
                    current = next;
                }
                catch
                {
                    next.Dispose();
                    throw;
                }
            }

            TerminalName = segments[^1];
            comparisonSegments[^1] = TerminalName;
            _targetHandle = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                current,
                TerminalName,
                allowMissing: true,
                write: writeTarget,
                denyDeleteSharing: fenceTargetNamespace,
                denyWriteSharing: fenceTargetNamespace,
                allowMultipleLinks: _allowMultipleLinksForProbe);
            if (_targetHandle is not null)
            {
                WorkspaceActionNativeFileSystem.RequireExactOpenedName(_targetHandle, TerminalName);
                TargetIdentity = WorkspaceActionNativeFileSystem.GetIdentity(_targetHandle);
                if (!TargetIdentity.Value.SameMount(rootIdentity))
                {
                    throw new IOException("Workspace target resides on another filesystem mount or volume.");
                }
            }
            RootIdentity = rootIdentity;
            ParentIdentity = _directories[^1].Identity;
            var comparerCanonical = string.Join('/', comparisonSegments);
            var ancestorProof = string.Join("/", _directories.Select(item => item.Identity.Fingerprint));
            TargetFingerprint = WorkspaceActionFingerprint.Compute(
                "embodysense.workspace-exact-target.v1",
                rootIdentity.Fingerprint,
                scopeId.Value,
                comparerCanonical,
                ancestorProof);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public WorkspaceActionScopeId ScopeId { get; }

    public string TerminalName { get; }

    public WorkspaceActionNativeFileStamp RootIdentity { get; }

    public WorkspaceActionNativeFileStamp ParentIdentity { get; }

    public WorkspaceActionNativeFileStamp? TargetIdentity { get; }

    public string TargetFingerprint { get; }

    public bool Exists => _targetHandle is not null;

    public SafeFileHandle ParentHandle => _directories[^1].Handle;

    public SafeFileHandle? TargetHandle => _targetHandle;

    public void ReleaseTargetHandle()
    {
        _targetHandle?.Dispose();
        _targetHandle = null;
    }

    public static WorkspaceActionRetainedTargetSession Open(
        string rootPath,
        WorkspaceActionScopeId scopeId,
        WorkspaceRelativeFileTarget target,
        bool writeTarget,
        bool fenceTargetNamespace = false,
        bool fenceDirectoryNamespace = false)
        => new(rootPath, scopeId, target, writeTarget, fenceTargetNamespace, fenceDirectoryNamespace, allowMultipleLinksForProbe: false);

    public static WorkspaceActionRetainedTargetSession OpenForProbe(
        string rootPath,
        WorkspaceActionScopeId scopeId,
        WorkspaceRelativeFileTarget target)
        => new(rootPath, scopeId, target, writeTarget: false, fenceTargetNamespace: false, fenceDirectoryNamespace: false, allowMultipleLinksForProbe: true);
    public Task<byte[]> ReadTargetBytesAsync(int maximumBytes, CancellationToken cancellationToken)
        => _targetHandle is null
            ? throw new FileNotFoundException("The exact workspace action target is absent.")
            : WorkspaceActionNativeFileSystem.ReadAllBytesAsync(
                _targetHandle,
                maximumBytes,
                cancellationToken,
                requireSingleLink: !_allowMultipleLinksForProbe);

    public async Task<bool> MatchesBeforeAsync(WorkspaceActionBeforeEvidence before, CancellationToken cancellationToken)
    {
        if (WorkspaceActionEvidenceContract.ValidateBefore(before) is not null
            || !string.Equals(before.ScopeId, ScopeId.Value, StringComparison.Ordinal)
            || !string.Equals(before.TargetReference, _target.Value, StringComparison.Ordinal)
            || !string.Equals(before.TargetFingerprint, TargetFingerprint, StringComparison.Ordinal)
            || !string.Equals(before.RootIdentityFingerprint, RootIdentity.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(before.ParentIdentityFingerprint, ParentIdentity.Fingerprint, StringComparison.Ordinal))
        {
            return false;
        }
        if (before.EntryKind == WorkspaceActionEntryKind.Absent)
        {
            using var named = WorkspaceActionNativeFileSystem.OpenRelativeFile(
                ParentHandle,
                TerminalName,
                allowMissing: true,
                write: false);
            return !Exists && named is null;
        }
        if (!Exists
            || TargetIdentity is null
            || !string.Equals(before.NativeIdentityFingerprint, TargetIdentity.Value.Fingerprint, StringComparison.Ordinal)
            || (_allowMultipleLinksForProbe && TargetIdentity.Value.LinkCount != 1))
        {
            return false;
        }
        using (var named = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            ParentHandle,
            TerminalName,
            allowMissing: true,
            write: false,
            allowMultipleLinks: _allowMultipleLinksForProbe))
        {
            if (named is null)
            {
                return false;
            }
            var namedIdentity = WorkspaceActionNativeFileSystem.GetIdentity(named);
            if (!namedIdentity.SameEntry(TargetIdentity.Value) || namedIdentity.LinkCount != 1)
            {
                return false;
            }
        }
        var bytes = await ReadTargetBytesAsync(WorkspaceActionContractLimits.MaxBeforeImageBytes, cancellationToken).ConfigureAwait(false);
        if (_allowMultipleLinksForProbe && WorkspaceActionNativeFileSystem.GetIdentity(_targetHandle!).LinkCount != 1)
        {
            return false;
        }
        var contentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return bytes.LongLength == before.ByteCount && string.Equals(contentHash, before.ContentHash, StringComparison.Ordinal);
    }

    public void RevalidateTerminalName()
    {
        using var named = WorkspaceActionNativeFileSystem.OpenRelativeFile(
            ParentHandle,
            TerminalName,
            allowMissing: false,
            write: false)
            ?? throw new IOException("The exact workspace target disappeared before the native commit boundary.");
        WorkspaceActionNativeFileSystem.RequireExactOpenedName(named, TerminalName);
    }

    public void RevalidateDirectories()
    {
        var rootIdentity = _directories[0].Identity;
        foreach (var (handle, identity, _) in _directories)
        {
            var current = WorkspaceActionNativeFileSystem.GetIdentity(handle);
            if (!current.SameEntry(identity)
                || !current.SameMount(rootIdentity)
                || !current.IsDirectory
                || current.IsReparsePoint)
            {
                throw new IOException("A retained workspace root, ancestor, or parent was substituted.");
            }
        }
        RevalidateDirectoryNamespace();
    }

    /// <summary>Proves that the configured root and every textual ancestor still resolve to the retained identities.</summary>
    public void RevalidateDirectoryNamespace()
    {
        var transient = new List<SafeFileHandle>(_directories.Count);
        try
        {
            var current = WorkspaceActionNativeFileSystem.OpenAbsoluteDirectory(_rootPath);
            transient.Add(current);
            if (!WorkspaceActionNativeFileSystem.GetIdentity(current).SameEntry(_directories[0].Identity))
            {
                throw new IOException("The configured workspace root no longer resolves to the retained root identity.");
            }
            for (var index = 1; index < _directories.Count; index++)
            {
                var next = WorkspaceActionNativeFileSystem.OpenRelativeDirectory(current, _directories[index].Name!);
                transient.Add(next);
                if (!WorkspaceActionNativeFileSystem.GetIdentity(next).SameEntry(_directories[index].Identity))
                {
                    throw new IOException("A workspace target ancestor name no longer resolves to its retained identity.");
                }
                current = next;
            }
        }
        finally
        {
            for (var index = transient.Count - 1; index >= 0; index--)
            {
                transient[index].Dispose();
            }
        }
    }

    public void Dispose()
    {
        _targetHandle?.Dispose();
        _targetHandle = null;
        for (var index = _directories.Count - 1; index >= 0; index--)
        {
            _directories[index].Handle.Dispose();
        }
        _directories.Clear();
    }
}
