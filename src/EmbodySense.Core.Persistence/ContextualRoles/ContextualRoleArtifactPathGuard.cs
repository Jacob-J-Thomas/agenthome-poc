using EmbodySense.Core.Application.ContextualRoles.Models;
using EmbodySense.Core.Persistence.ContextualRoles.Models;
using EmbodySense.Core.Persistence.Loops;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EmbodySense.Core.Persistence.ContextualRoles;

internal sealed class ContextualRoleArtifactPathGuard : IDisposable
{
    private const int MaxArtifactBytes = 64 * 1024;
    private const int MaxDirectoryEntries = ContextualRoleRevisionStoreOptions.MaximumOperationArtifacts * 4;
    private const int NativeDirectoryBufferBytes = 64 * 1024;
    private const string MutationDiagnosticDataKey = "EmbodySense.ContextualRoleMutationDiagnostic";
    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly ContextualRoleStorePaths _paths;
    private readonly Func<ContextualRolePhysicalPersistenceBoundary, CancellationToken, ValueTask>? _boundaryObserver;
    private readonly StringComparison _pathComparison;
    private readonly string _agentPath;
    private readonly string _agentName;
    private readonly object _directoryGate = new();
    private readonly SafeFileHandle _workspaceHandle;
    private readonly NativeIdentity _workspaceIdentity;
    private SafeFileHandle? _agentHandle;
    private NativeIdentity _agentIdentity;
    private SafeFileHandle? _rootHandle;
    private NativeIdentity _rootIdentity;
    private SafeFileHandle? _revisionsHandle;
    private NativeIdentity _revisionsIdentity;
    private SafeFileHandle? _statesHandle;
    private NativeIdentity _statesIdentity;
    private SafeFileHandle? _operationsHandle;
    private NativeIdentity _operationsIdentity;
    private SafeFileHandle? _proofsHandle;
    private NativeIdentity _proofsIdentity;

    public ContextualRoleArtifactPathGuard(ContextualRoleStorePaths paths, Func<ContextualRolePhysicalPersistenceBoundary, CancellationToken, ValueTask>? boundaryObserver)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _boundaryObserver = boundaryObserver;
        _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Directory.Exists(_paths.WorkspaceRoot))
        {
            throw new DirectoryNotFoundException("The contextual-role workspace root does not exist.");
        }

        _agentPath = Path.GetDirectoryName(_paths.Root) ?? throw new InvalidOperationException("The contextual-role store has no agent parent directory.");
        _agentName = Path.GetFileName(Path.TrimEndingDirectorySeparator(_agentPath));
        if (!string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(_agentPath)), Path.TrimEndingDirectorySeparator(_paths.WorkspaceRoot), _pathComparison))
        {
            throw new InvalidOperationException("The contextual-role agent directory is not a direct child of its canonical workspace root.");
        }

        _workspaceHandle = OpenAbsoluteDirectory(_paths.WorkspaceRoot, allowMissing: false) ?? throw new DirectoryNotFoundException("The contextual-role workspace root does not exist.");
        _workspaceIdentity = ValidateDirectory(_workspaceHandle, "contextual-role workspace root");
        RootCreationTimeUtcTicks = Directory.GetCreationTimeUtc(_paths.WorkspaceRoot).Ticks;
        var canonicalRoot = OperatingSystem.IsWindows() ? _paths.WorkspaceRoot.ToUpperInvariant() : _paths.WorkspaceRoot;
        CanonicalRootHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRoot))).ToLowerInvariant();
        VerifyWorkspaceIdentity();
    }

    public string CanonicalRootHash { get; }
    public long RootCreationTimeUtcTicks { get; }

    public bool StoreExists()
    {
        lock (_directoryGate)
        {
            VerifyWorkspaceMapping();
            if (_agentHandle is null)
            {
                _agentHandle = OpenRelativeDirectory(_workspaceHandle, _agentName, allowMissing: true, create: false, out _);
                if (_agentHandle is null)
                {
                    return false;
                }

                _agentIdentity = ValidateDirectory(_agentHandle, "contextual-role agent directory");
            }

            VerifyDirectoryMapping(_agentPath, _agentHandle, _agentIdentity);
            if (_rootHandle is null)
            {
                _rootHandle = OpenRelativeDirectory(_agentHandle, Path.GetFileName(_paths.Root), allowMissing: true, create: false, out _);
                if (_rootHandle is null)
                {
                    return false;
                }

                _rootIdentity = ValidateDirectory(_rootHandle, "contextual-role store root");
            }

            VerifyAllMappings();
            return true;
        }
    }

    public void PrepareRoots()
    {
        lock (_directoryGate)
        {
            VerifyWorkspaceMapping();
            EnsureDirectory(ref _agentHandle, ref _agentIdentity, _workspaceHandle, _agentName, _agentPath);
            EnsureDirectory(ref _rootHandle, ref _rootIdentity, _agentHandle!, Path.GetFileName(_paths.Root), _paths.Root);
            EnsureDirectory(ref _revisionsHandle, ref _revisionsIdentity, _rootHandle!, Path.GetFileName(_paths.Revisions), _paths.Revisions);
            EnsureDirectory(ref _statesHandle, ref _statesIdentity, _rootHandle!, Path.GetFileName(_paths.States), _paths.States);
            EnsureDirectory(ref _operationsHandle, ref _operationsIdentity, _rootHandle!, Path.GetFileName(_paths.Operations), _paths.Operations);
            EnsureDirectory(ref _proofsHandle, ref _proofsIdentity, _rootHandle!, Path.GetFileName(_paths.Proofs), _paths.Proofs);
            VerifyAllMappings();
        }
    }

    public FileStream? TryAcquireMutationLock()
    {
        PrepareRoots();
        var (directory, _, name) = ResolveFile(_paths.Lock);
        var handle = OpenRelativeFile(directory, name, allowMissing: false, create: true, exclusiveCreate: false, write: true) ?? throw new IOException("The contextual-role mutation lock could not be created.");
        var identity = ValidateRegularFile(handle, "contextual-role mutation lock");
        var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
        if (!CustomLoopCrossProcessFileLock.TryAcquire(stream))
        {
            stream.Dispose();
            return null;
        }

        ValidateIdentity(handle, identity, requireSingleLink: true, "contextual-role mutation lock");
        FlushFile(handle);
        FlushDirectory(directory);
        VerifyAllMappings();
        return stream;
    }

    public bool FileExists(string path) => Diagnose(ContextualRolePersistenceDiagnosticStage.ArtifactExistenceCheck, () => FileExistsCore(path));

    private bool FileExistsCore(string path)
    {
        PrepareRoots();
        var (directory, _, name) = ResolveFile(path);
        using var handle = OpenRelativeFile(directory, name, allowMissing: true, create: false, exclusiveCreate: false, write: false);
        if (handle is null)
        {
            return false;
        }

        _ = ValidateRegularFile(handle, "contextual-role artifact");
        VerifyAllMappings();
        return true;
    }

    public long GetFileLength(string path)
    {
        PrepareRoots();
        var (directory, _, name) = ResolveFile(path);
        using var handle = OpenRelativeFile(directory, name, allowMissing: false, create: false, exclusiveCreate: false, write: false) ?? throw new FileNotFoundException("The contextual-role artifact does not exist.", path);
        var identity = ValidateRegularFile(handle, "contextual-role artifact");
        var length = RandomAccess.GetLength(handle);
        ValidateIdentity(handle, identity, requireSingleLink: true, "contextual-role artifact");
        VerifyAllMappings();
        return length;
    }

    public async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        PrepareRoots();
        var (directory, _, name) = ResolveFile(path);
        VerifyAllMappings();
        await ObserveAsync(ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativeRead, cancellationToken);
        using var handle = OpenRelativeFile(directory, name, allowMissing: false, create: false, exclusiveCreate: false, write: false) ?? throw new FileNotFoundException("The contextual-role artifact does not exist.", path);
        var identity = ValidateRegularFile(handle, "contextual-role artifact");
        await using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        if (stream.Length > MaxArtifactBytes)
        {
            throw new FormatException("A contextual-role artifact exceeds the bounded 64 KiB schema-1 limit.");
        }

        using var memory = new MemoryStream(checked((int)stream.Length));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (memory.Length + read > MaxArtifactBytes)
            {
                throw new FormatException("A contextual-role artifact grew beyond the bounded 64 KiB schema-1 limit while being read.");
            }

            memory.Write(buffer, 0, read);
        }

        ValidateIdentity(handle, identity, requireSingleLink: true, "contextual-role artifact");
        VerifyAllMappings();
        return memory.ToArray();
    }

    public async Task WriteImmutableAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        => await WriteAsync(path, bytes, overwrite: false, cancellationToken);

    public async Task WriteProjectionAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        => await WriteAsync(path, bytes, overwrite: true, cancellationToken);

    public IReadOnlyList<string> EnumerateJsonFiles(string directory) => Diagnose(ContextualRolePersistenceDiagnosticStage.ArtifactEnumeration, () => EnumerateJsonFilesCore(directory));

    private IReadOnlyList<string> EnumerateJsonFilesCore(string directory)
    {
        PrepareRoots();
        var (handle, _, canonicalPath) = ResolveDirectory(directory);
        VerifyAllMappings();
        var paths = new List<string>();
        foreach (var (name, isDirectory) in EnumerateDirectoryEntries(handle, observeValidationBoundary: true))
        {
            if (isDirectory)
            {
                throw new FormatException("Contextual-role persistence contains an unknown nested artifact directory.");
            }

            if (!name.EndsWith(".json", StringComparison.Ordinal))
            {
                throw new FormatException("Contextual-role persistence contains an unknown artifact file.");
            }

            paths.Add(Path.Combine(canonicalPath, name));
        }

        VerifyAllMappings();
        return paths.Order(StringComparer.Ordinal).ToArray();
    }

    public void ValidateKnownLayout()
    {
        PrepareRoots();
        VerifyAllMappings();
        var knownDirectories = new HashSet<string>([Path.GetFileName(_paths.Revisions), Path.GetFileName(_paths.States), Path.GetFileName(_paths.Operations), Path.GetFileName(_paths.Proofs)], OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var (name, isDirectory) in EnumerateDirectoryEntries(_rootHandle!, observeValidationBoundary: true))
        {
            if (isDirectory)
            {
                if (!knownDirectories.Contains(name))
                {
                    throw new FormatException("Contextual-role persistence contains an unknown top-level artifact directory.");
                }

                continue;
            }

            if (!string.Equals(name, Path.GetFileName(_paths.Anchor), StringComparison.Ordinal) && !string.Equals(name, Path.GetFileName(_paths.Lock), StringComparison.Ordinal))
            {
                throw new FormatException("Contextual-role persistence contains an unknown top-level artifact file.");
            }
        }

        VerifyAllMappings();
    }

    public long CountArtifactBytes()
    {
        PrepareRoots();
        long total = FileExists(_paths.Anchor) ? GetFileLength(_paths.Anchor) : 0;
        foreach (var path in EnumerateJsonFiles(_paths.Revisions).Concat(EnumerateJsonFiles(_paths.States)).Concat(EnumerateJsonFiles(_paths.Operations)).Concat(EnumerateJsonFiles(_paths.Proofs)))
        {
            total = checked(total + GetFileLength(path));
        }

        return total;
    }

    public void CleanupTemporaryArtifacts() => Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFileCleanup, CleanupTemporaryArtifactsCore);

    private void CleanupTemporaryArtifactsCore()
    {
        PrepareRoots();
        CleanupTemporaryArtifacts(_rootHandle!);
        CleanupTemporaryArtifacts(_revisionsHandle!);
        CleanupTemporaryArtifacts(_statesHandle!);
        CleanupTemporaryArtifacts(_operationsHandle!);
        CleanupTemporaryArtifacts(_proofsHandle!);
        VerifyAllMappings();
    }

    public void VerifyWorkspaceIdentity()
    {
        lock (_directoryGate)
        {
            VerifyAllMappings();
            if (Directory.GetCreationTimeUtc(_paths.WorkspaceRoot).Ticks != RootCreationTimeUtcTicks)
            {
                throw new InvalidOperationException("The physical contextual-role workspace root changed during persistence.");
            }
        }
    }

    public void Dispose()
    {
        lock (_directoryGate)
        {
            _proofsHandle?.Dispose();
            _proofsHandle = null;
            _operationsHandle?.Dispose();
            _operationsHandle = null;
            _statesHandle?.Dispose();
            _statesHandle = null;
            _revisionsHandle?.Dispose();
            _revisionsHandle = null;
            _rootHandle?.Dispose();
            _rootHandle = null;
            _agentHandle?.Dispose();
            _agentHandle = null;
            _workspaceHandle.Dispose();
        }
    }

    internal static ContextualRoleRevisionMutationDiagnostic? GetMutationDiagnostic(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data[MutationDiagnosticDataKey] is ContextualRoleRevisionMutationDiagnostic diagnostic)
            {
                return diagnostic;
            }
        }

        return null;
    }

    private async Task WriteAsync(string path, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length > MaxArtifactBytes)
        {
            throw new InvalidOperationException("A contextual-role artifact exceeds the bounded 64 KiB schema-1 limit.");
        }

        Diagnose(ContextualRolePersistenceDiagnosticStage.RootPreparation, PrepareRoots);
        var (directory, _, name) = Diagnose(ContextualRolePersistenceDiagnosticStage.RootPreparation, () => ResolveFile(path));
        Diagnose(ContextualRolePersistenceDiagnosticStage.RootPreparation, VerifyAllMappings);
        if (overwrite)
        {
            using var existing = Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetOpen, () => OpenRelativeFile(directory, name, allowMissing: true, create: false, exclusiveCreate: false, write: false));
            if (existing is not null)
            {
                _ = Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetIdentityValidation, () => ValidateRegularFile(existing, "contextual-role projection target"));
            }
        }

        var temporaryName = $".{name}.{Guid.NewGuid():N}.tmp";
        using var temporaryHandle = Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFileOpen, () => OpenRelativeFile(directory, temporaryName, allowMissing: false, create: true, exclusiveCreate: true, write: true) ?? throw new IOException("The contextual-role temporary artifact could not be created."));
        var temporaryIdentity = Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFileIdentityValidation, () => ValidateRegularFile(temporaryHandle, "contextual-role temporary artifact"));
        var published = false;
        await using var stream = Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFileOpen, () => new FileStream(temporaryHandle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false));
        try
        {
            await DiagnoseAsync(ContextualRolePersistenceDiagnosticStage.TemporaryFileWrite, async () =>
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            });
            Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFileFlush, () => FlushFile(temporaryHandle));
            Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFilePostFlushIdentityValidation, () => ValidateIdentity(temporaryHandle, temporaryIdentity, requireSingleLink: true, "contextual-role temporary artifact"));
            await DiagnoseAsync(ContextualRolePersistenceDiagnosticStage.PrePublicationObservation, async () => await ObserveAsync(ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativePublication, cancellationToken));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublicationRename, () => RenameRelative(temporaryHandle, directory, temporaryName, name, overwrite));
            published = true;

            using var target = Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetOpen, () => OpenRelativeFile(directory, name, allowMissing: false, create: false, exclusiveCreate: false, write: true) ?? throw new ContextualRolePersistenceUnavailableException("The published contextual-role target could not be reopened by retained directory handle."));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetIdentityValidation, () => ValidateIdentity(target, temporaryIdentity, requireSingleLink: true, "published contextual-role target"));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetContentValidation, () => ValidatePublishedContent(target, bytes));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetFlush, () => FlushFile(target));
            await DiagnoseAsync(ContextualRolePersistenceDiagnosticStage.PostTargetFlushObservation, async () => await ObserveAsync(ContextualRolePhysicalPersistenceBoundary.AfterTargetFlushBeforeDirectoryFlush, cancellationToken));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetIdentityValidation, () => ValidateIdentity(target, temporaryIdentity, requireSingleLink: true, "published contextual-role target"));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetContentValidation, () => ValidatePublishedContent(target, bytes));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetFlush, () => FlushFile(target));
            Diagnose(ContextualRolePersistenceDiagnosticStage.ParentDirectoryFlush, () => FlushDirectory(directory));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetIdentityValidation, () => ValidateIdentity(target, temporaryIdentity, requireSingleLink: true, "published contextual-role target"));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedTargetContentValidation, () => ValidatePublishedContent(target, bytes));
            Diagnose(ContextualRolePersistenceDiagnosticStage.PublishedMappingValidation, VerifyAllMappings);
        }
        catch (Exception exception) when (published && exception is not ContextualRolePublicationAmbiguousException)
        {
            throw new ContextualRolePublicationAmbiguousException("A contextual-role rename occurred but its exact target, parent-directory barrier, or retained path identity could not be proved.", exception);
        }
        finally
        {
            if (!published)
            {
                Diagnose(ContextualRolePersistenceDiagnosticStage.TemporaryFileCleanup, () =>
                {
                    DeleteExactTemporary(directory, temporaryName, temporaryHandle, temporaryIdentity);
                    FlushDirectory(directory);
                });
            }
        }
    }

    private void CleanupTemporaryArtifacts(SafeFileHandle directory)
    {
        VerifyAllMappings();
        var deleted = false;
        foreach (var (name, isDirectory) in EnumerateDirectoryEntries(directory, observeValidationBoundary: false))
        {
            if (isDirectory || !name.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            using var handle = OpenRelativeFile(directory, name, allowMissing: true, create: false, exclusiveCreate: false, write: true);
            if (handle is null)
            {
                throw new FormatException("A contextual-role temporary artifact disappeared during guarded cleanup.");
            }

            var identity = ValidateRegularFile(handle, "contextual-role temporary artifact");
            DeleteExactTemporary(directory, name, handle, identity);
            deleted = true;
        }

        if (deleted)
        {
            FlushDirectory(directory);
        }
    }

    private IReadOnlyList<(string Name, bool IsDirectory)> EnumerateDirectoryEntries(SafeFileHandle directory, bool observeValidationBoundary)
    {
        var expected = ValidateDirectory(directory, "contextual-role enumeration directory");
        if (observeValidationBoundary)
        {
            ObserveSynchronously(ContextualRolePhysicalPersistenceBoundary.BeforeHandleRelativeValidationEnumeration);
        }

        try
        {
            var names = EnumerateNativeDirectoryNames(directory);
            if (names.Count > MaxDirectoryEntries)
            {
                throw new FormatException("A contextual-role artifact directory exceeds its bounded entry limit.");
            }

            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var uniqueNames = new HashSet<string>(comparer);
            var entries = new List<(string Name, bool IsDirectory)>(names.Count);
            foreach (var name in names)
            {
                EnsureSimpleName(name);
                if (!uniqueNames.Add(name))
                {
                    throw new FormatException("A contextual-role artifact directory contains an ambiguous duplicate entry name.");
                }

                using var entry = OpenRelativeEntry(directory, name);
                var identity = GetIdentity(entry);
                if (identity.IsReparsePoint || identity.LinkCount == 0 || !identity.IsDirectory && (!identity.IsRegularFile || identity.LinkCount != 1))
                {
                    throw new InvalidOperationException("A contextual-role directory entry is not one retained no-follow directory or single-link regular file.");
                }

                ValidateIdentity(entry, identity, requireSingleLink: !identity.IsDirectory, "contextual-role enumerated entry");
                entries.Add((name, identity.IsDirectory));
            }

            ValidateIdentity(directory, expected, requireSingleLink: false, "contextual-role enumeration directory");
            return entries;
        }
        finally
        {
            if (observeValidationBoundary)
            {
                ObserveSynchronously(ContextualRolePhysicalPersistenceBoundary.AfterHandleRelativeValidationEnumeration);
            }
        }
    }

    private void EnsureDirectory(ref SafeFileHandle? handle, ref NativeIdentity identity, SafeFileHandle parent, string name, string path)
    {
        if (handle is null)
        {
            handle = OpenRelativeDirectory(parent, name, allowMissing: false, create: true, out var created) ?? throw new DirectoryNotFoundException("A contextual-role persistence directory could not be created.");
            identity = ValidateDirectory(handle, "contextual-role persistence directory");
            if (created)
            {
                FlushDirectory(handle);
                FlushDirectory(parent);
            }
        }

        VerifyDirectoryMapping(path, handle, identity);
    }

    private (SafeFileHandle Handle, NativeIdentity Identity, string Path) ResolveDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(fullPath, Path.GetFullPath(_paths.Root), _pathComparison))
        {
            return (_rootHandle!, _rootIdentity, _paths.Root);
        }

        if (string.Equals(fullPath, Path.GetFullPath(_paths.Revisions), _pathComparison))
        {
            return (_revisionsHandle!, _revisionsIdentity, _paths.Revisions);
        }

        if (string.Equals(fullPath, Path.GetFullPath(_paths.States), _pathComparison))
        {
            return (_statesHandle!, _statesIdentity, _paths.States);
        }

        if (string.Equals(fullPath, Path.GetFullPath(_paths.Operations), _pathComparison))
        {
            return (_operationsHandle!, _operationsIdentity, _paths.Operations);
        }

        if (string.Equals(fullPath, Path.GetFullPath(_paths.Proofs), _pathComparison))
        {
            return (_proofsHandle!, _proofsIdentity, _paths.Proofs);
        }

        throw new InvalidOperationException("A contextual-role operation targeted an unknown persistence directory.");
    }

    private (SafeFileHandle Handle, NativeIdentity Identity, string Name) ResolveFile(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("Contextual-role artifact path has no parent directory.");
        var (handle, identity, _) = ResolveDirectory(parent);
        var name = Path.GetFileName(fullPath);
        EnsureSimpleName(name);
        return (handle, identity, name);
    }

    private void VerifyAllMappings()
    {
        VerifyWorkspaceMapping();
        if (_agentHandle is not null)
        {
            VerifyDirectoryMapping(_agentPath, _agentHandle, _agentIdentity);
        }

        if (_rootHandle is not null)
        {
            VerifyDirectoryMapping(_paths.Root, _rootHandle, _rootIdentity);
        }

        if (_revisionsHandle is not null)
        {
            VerifyDirectoryMapping(_paths.Revisions, _revisionsHandle, _revisionsIdentity);
        }

        if (_statesHandle is not null)
        {
            VerifyDirectoryMapping(_paths.States, _statesHandle, _statesIdentity);
        }

        if (_operationsHandle is not null)
        {
            VerifyDirectoryMapping(_paths.Operations, _operationsHandle, _operationsIdentity);
        }

        if (_proofsHandle is not null)
        {
            VerifyDirectoryMapping(_paths.Proofs, _proofsHandle, _proofsIdentity);
        }
    }

    private void VerifyWorkspaceMapping() => VerifyDirectoryMapping(_paths.WorkspaceRoot, _workspaceHandle, _workspaceIdentity);

    private static void VerifyDirectoryMapping(string path, SafeFileHandle retained, NativeIdentity expected)
    {
        using var current = OpenAbsoluteDirectory(path, allowMissing: false) ?? throw new InvalidOperationException("A retained contextual-role directory is no longer reachable at its canonical path.");
        var actual = ValidateDirectory(current, "contextual-role directory mapping");
        ValidateIdentity(retained, expected, requireSingleLink: false, "retained contextual-role directory");
        if (!SameIdentity(actual, expected))
        {
            throw new InvalidOperationException("A contextual-role directory path was replaced after its physical handle was retained.");
        }
    }

    private async ValueTask ObserveAsync(ContextualRolePhysicalPersistenceBoundary boundary, CancellationToken cancellationToken)
    {
        if (_boundaryObserver is { } observer)
        {
            await observer(boundary, cancellationToken);
        }
    }

    private void ObserveSynchronously(ContextualRolePhysicalPersistenceBoundary boundary)
    {
        if (_boundaryObserver is { } observer)
        {
            observer(boundary, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
    }

    private static void EnsureSimpleName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['\0', Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException("A contextual-role handle-relative name was not one bounded path segment.");
        }
    }

    private static NativeIdentity ValidateDirectory(SafeFileHandle handle, string description)
    {
        var identity = GetIdentity(handle);
        if (!identity.IsDirectory || identity.IsReparsePoint || identity.LinkCount == 0)
        {
            throw new InvalidOperationException($"The {description} is not one retained no-follow physical directory.");
        }

        return identity;
    }

    private static NativeIdentity ValidateRegularFile(SafeFileHandle handle, string description)
    {
        var identity = GetIdentity(handle);
        if (!identity.IsRegularFile || identity.IsDirectory || identity.IsReparsePoint || identity.LinkCount != 1)
        {
            throw new InvalidOperationException($"The {description} is not one single-link no-follow regular file.");
        }

        return identity;
    }

    private static void ValidateIdentity(SafeFileHandle handle, NativeIdentity expected, bool requireSingleLink, string description)
    {
        var actual = GetIdentity(handle);
        if (!SameIdentity(actual, expected) || actual.IsReparsePoint || actual.IsDirectory != expected.IsDirectory || actual.IsRegularFile != expected.IsRegularFile || requireSingleLink && actual.LinkCount != 1 || !requireSingleLink && actual.LinkCount == 0)
        {
            throw new InvalidOperationException($"The {description} changed identity, type, or link count during guarded access.");
        }
    }

    private static void ValidatePublishedContent(SafeFileHandle handle, ReadOnlySpan<byte> expected)
    {
        if (RandomAccess.GetLength(handle) != expected.Length)
        {
            throw new InvalidOperationException("The published contextual-role target length changed during guarded publication.");
        }

        var actual = GC.AllocateUninitializedArray<byte>(expected.Length);
        var offset = 0;
        while (offset < actual.Length)
        {
            var read = RandomAccess.Read(handle, actual.AsSpan(offset), offset);
            if (read == 0)
            {
                throw new InvalidOperationException("The published contextual-role target ended before its expected durable content.");
            }

            offset += read;
        }

        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidOperationException("The published contextual-role target content changed during guarded publication.");
        }
    }

    private static void Diagnose(ContextualRolePersistenceDiagnosticStage stage, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            AttachMutationDiagnostic(exception, stage);
            throw;
        }
    }

    private static T Diagnose<T>(ContextualRolePersistenceDiagnosticStage stage, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception)
        {
            AttachMutationDiagnostic(exception, stage);
            throw;
        }
    }

    private static async Task DiagnoseAsync(ContextualRolePersistenceDiagnosticStage stage, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            AttachMutationDiagnostic(exception, stage);
            throw;
        }
    }

    private static void AttachMutationDiagnostic(Exception exception, ContextualRolePersistenceDiagnosticStage stage)
    {
        var native = FindNativeException(exception);
        exception.Data[MutationDiagnosticDataKey] = new ContextualRoleRevisionMutationDiagnostic(stage, native?.ErrorKind ?? ContextualRoleNativeErrorKind.None, native?.ErrorCode);
    }

    private static ContextualRoleNativeIOException? FindNativeException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is ContextualRoleNativeIOException native)
            {
                return native;
            }
        }

        return null;
    }

    // Native adapters are exercised through public behavior on each supported host; per-host coverage cannot execute mutually exclusive Windows, Linux, and macOS branches.
    [ExcludeFromCodeCoverage]
    private static SafeFileHandle? OpenAbsoluteDirectory(string path, bool allowMissing)
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                return handle;
            }

            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            if (allowMissing && error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return null;
            }

            throw NativeIOException("CreateFile directory open", error);
        }

        var descriptor = UnixOpen(path, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var errno = Marshal.GetLastPInvokeError();
        if (allowMissing && errno == UnixNoEntry)
        {
            return null;
        }

        if (errno is UnixNotDirectory || errno == UnixSymbolicLinkLoop)
        {
            throw new InvalidOperationException("A contextual-role directory path contains a symbolic link or non-directory substitution.");
        }

        throw NativeIOException("open directory", errno);
    }

    [ExcludeFromCodeCoverage]
    private static IReadOnlyList<string> EnumerateNativeDirectoryNames(SafeFileHandle directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return EnumerateWindowsDirectoryNames(directory);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return EnumerateUnixDirectoryNames(directory);
        }

        throw new PlatformNotSupportedException("Retained contextual-role directory enumeration is supported only on Windows, Linux, and macOS.");
    }

    [ExcludeFromCodeCoverage]
    private static IReadOnlyList<string> EnumerateWindowsDirectoryNames(SafeFileHandle directory)
    {
        var buffer = Marshal.AllocHGlobal(NativeDirectoryBufferBytes);
        try
        {
            var names = new List<string>();
            var restartScan = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var ioStatus, buffer, NativeDirectoryBufferBytes, FileDirectoryInformation, 0, IntPtr.Zero, restartScan ? (byte)1 : (byte)0);
                restartScan = false;
                if (unchecked((uint)status) == StatusNoMoreFiles)
                {
                    break;
                }

                if (status < 0)
                {
                    var unsignedStatus = unchecked((uint)status);
                    throw new ContextualRoleNativeIOException($"NtQueryDirectoryFile failed closed with NTSTATUS 0x{unsignedStatus:x8}.", ContextualRoleNativeErrorKind.NtStatus, unsignedStatus);
                }

                var returnedBytes = ioStatus.Information.ToInt64();
                if (returnedBytes is <= 0 or > NativeDirectoryBufferBytes)
                {
                    throw new FormatException("Windows returned an invalid bounded contextual-role directory buffer length.");
                }

                var offset = 0;
                while (true)
                {
                    if (returnedBytes - offset < Marshal.SizeOf<FileDirectoryInformationHeader>())
                    {
                        throw new FormatException("Windows returned a truncated contextual-role directory entry.");
                    }

                    var header = Marshal.PtrToStructure<FileDirectoryInformationHeader>(buffer + offset);
                    if (header.NextEntryOffset > int.MaxValue || header.FileNameLength > int.MaxValue)
                    {
                        throw new FormatException("Windows returned an oversized contextual-role directory entry field.");
                    }

                    var nextOffset = (int)header.NextEntryOffset;
                    var nameBytes = (int)header.FileNameLength;
                    if (nameBytes is <= 0 or > WindowsMaximumFileNameBytes || (nameBytes & 1) != 0 || returnedBytes - offset - WindowsFileDirectoryInformationNameOffset < nameBytes)
                    {
                        throw new FormatException("Windows returned an invalid contextual-role directory entry name length.");
                    }

                    var name = Marshal.PtrToStringUni(buffer + offset + WindowsFileDirectoryInformationNameOffset, nameBytes / 2) ?? throw new FormatException("Windows returned an invalid contextual-role directory entry name.");
                    if (name is not "." and not "..")
                    {
                        names.Add(name);
                        if (names.Count > MaxDirectoryEntries)
                        {
                            throw new FormatException("A contextual-role artifact directory exceeds its bounded entry limit.");
                        }
                    }

                    if (nextOffset == 0)
                    {
                        break;
                    }

                    if (nextOffset < WindowsFileDirectoryInformationNameOffset || (nextOffset & 7) != 0 || offset + nextOffset >= returnedBytes)
                    {
                        throw new FormatException("Windows returned an invalid contextual-role directory entry offset.");
                    }

                    offset += nextOffset;
                }
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [ExcludeFromCodeCoverage]
    private static IReadOnlyList<string> EnumerateUnixDirectoryNames(SafeFileHandle directory)
    {
        var duplicate = UnixDuplicate(directory.DangerousGetHandle().ToInt32());
        if (duplicate < 0)
        {
            throw NativeIOException("dup directory descriptor", Marshal.GetLastPInvokeError());
        }

        var stream = UnixFdOpenDirectory(duplicate);
        if (stream == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = UnixClose(duplicate);
            throw NativeIOException("fdopendir", error);
        }

        try
        {
            UnixRewindDirectory(stream);
            var names = new List<string>();
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = UnixReadDirectory(stream);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw NativeIOException("readdir", error);
                    }

                    break;
                }

                var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, UnixDirectoryRecordLengthOffset));
                var nameOffset = OperatingSystem.IsMacOS() ? MacDirectoryNameOffset : LinuxDirectoryNameOffset;
                if (recordLength <= nameOffset || recordLength > UnixMaximumDirectoryRecordBytes)
                {
                    throw new FormatException("Unix returned an invalid contextual-role directory entry length.");
                }

                var availableNameBytes = recordLength - nameOffset;
                var nameLength = 0;
                while (nameLength < availableNameBytes && Marshal.ReadByte(entry, nameOffset + nameLength) != 0)
                {
                    nameLength++;
                }

                if (nameLength == availableNameBytes || nameLength > UnixMaximumFileNameBytes)
                {
                    throw new FormatException("Unix returned an unterminated or oversized contextual-role directory entry name.");
                }

                var nameBytes = new byte[nameLength];
                Marshal.Copy(entry + nameOffset, nameBytes, 0, nameLength);
                string name;
                try
                {
                    name = _strictUtf8.GetString(nameBytes);
                }
                catch (DecoderFallbackException exception)
                {
                    throw new FormatException("Unix returned a contextual-role directory entry name that is not valid UTF-8.", exception);
                }

                if (name is not "." and not "..")
                {
                    names.Add(name);
                    if (names.Count > MaxDirectoryEntries)
                    {
                        throw new FormatException("A contextual-role artifact directory exceeds its bounded entry limit.");
                    }
                }
            }

            return names;
        }
        finally
        {
            if (UnixCloseDirectory(stream) != 0)
            {
                throw NativeIOException("closedir", Marshal.GetLastPInvokeError());
            }
        }
    }

    private static SafeFileHandle OpenRelativeEntry(SafeFileHandle parent, string name)
    {
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            return NtCreateRelative(parent, name, GenericRead | SynchronizeAccess, NtOpen, NtSynchronousIoNonAlert | NtOpenReparsePoint, out _, allowMissing: false) ?? throw new FormatException("A contextual-role directory entry disappeared during guarded enumeration.");
        }

        var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, UnixReadOnly | UnixNonBlocking | UnixNoFollow | UnixCloseOnExec, 0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == UnixNoEntry)
        {
            throw new FormatException("A contextual-role directory entry disappeared during guarded enumeration.");
        }

        if (error is UnixNotDirectory || error == UnixSymbolicLinkLoop)
        {
            throw new InvalidOperationException("A contextual-role directory entry is a symbolic link or invalid substitution.");
        }

        throw NativeIOException("openat enumerated entry", error);
    }

    private static SafeFileHandle? OpenRelativeDirectory(SafeFileHandle parent, string name, bool allowMissing, bool create, out bool created)
    {
        EnsureSimpleName(name);
        created = false;
        if (OperatingSystem.IsWindows())
        {
            var disposition = create ? NtOpenIf : NtOpen;
            var handle = NtCreateRelative(parent, name, GenericRead | GenericWrite | DeleteAccess | SynchronizeAccess, disposition, NtDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | NtWriteThrough, out var information, allowMissing);
            created = handle is not null && information == NtCreated;
            return handle;
        }

        if (create && UnixMkdirAt(parent.DangerousGetHandle().ToInt32(), name, Convert.ToUInt32("700", 8)) == 0)
        {
            created = true;
        }
        else if (create)
        {
            var mkdirError = Marshal.GetLastPInvokeError();
            if (mkdirError != UnixAlreadyExists)
            {
                throw NativeIOException("mkdirat", mkdirError);
            }
        }

        var descriptor = UnixOpenAt(parent.DangerousGetHandle().ToInt32(), name, UnixReadOnly | UnixDirectory | UnixNoFollow | UnixCloseOnExec, 0);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (allowMissing && error == UnixNoEntry)
        {
            return null;
        }

        if (error is UnixNotDirectory || error == UnixSymbolicLinkLoop)
        {
            throw new InvalidOperationException("A contextual-role directory entry is a symbolic link or non-directory substitution.");
        }

        throw NativeIOException("openat directory", error);
    }

    private static SafeFileHandle? OpenRelativeFile(SafeFileHandle parent, string name, bool allowMissing, bool create, bool exclusiveCreate, bool write)
    {
        EnsureSimpleName(name);
        if (OperatingSystem.IsWindows())
        {
            var disposition = exclusiveCreate ? NtCreate : create ? NtOpenIf : NtOpen;
            var access = write ? GenericRead | GenericWrite | DeleteAccess | SynchronizeAccess : GenericRead | SynchronizeAccess;
            return NtCreateRelative(parent, name, access, disposition, NtNonDirectoryFile | NtSynchronousIoNonAlert | NtOpenReparsePoint | NtWriteThrough, out _, allowMissing);
        }

        var flags = (write ? UnixReadWrite : UnixReadOnly) | UnixNoFollow | UnixCloseOnExec;
        if (create)
        {
            flags |= UnixCreate;
        }

        if (exclusiveCreate)
        {
            flags |= UnixExclusive;
        }

        var parentDescriptor = parent.DangerousGetHandle().ToInt32();
        var descriptor = UnixOpenAt(parentDescriptor, name, create && !exclusiveCreate ? flags | UnixExclusive : flags, Convert.ToInt32("600", 8));
        var newlyCreated = descriptor >= 0 && create;
        if (descriptor < 0 && create && !exclusiveCreate && Marshal.GetLastPInvokeError() == UnixAlreadyExists)
        {
            descriptor = UnixOpenAt(parentDescriptor, name, (write ? UnixReadWrite : UnixReadOnly) | UnixNoFollow | UnixCloseOnExec, 0);
            newlyCreated = false;
        }

        if (descriptor >= 0)
        {
            if (newlyCreated && UnixFchmod(descriptor, Convert.ToInt32("600", 8)) != 0)
            {
                var chmodError = Marshal.GetLastPInvokeError();
                _ = UnixClose(descriptor);
                throw NativeIOException("fchmod newly created file", chmodError);
            }

            return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        }

        var error = Marshal.GetLastPInvokeError();
        if (allowMissing && error == UnixNoEntry)
        {
            return null;
        }

        if (error is UnixNotDirectory || error == UnixSymbolicLinkLoop)
        {
            throw new InvalidOperationException("A contextual-role artifact entry is a symbolic link or directory substitution.");
        }

        throw NativeIOException("openat file", error);
    }

    [ExcludeFromCodeCoverage]
    private static SafeFileHandle? NtCreateRelative(SafeFileHandle parent, string name, uint access, uint disposition, uint options, out long information, bool allowMissing)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var nameBytes = checked(name.Length * sizeof(char));
            var unicode = new UnicodeString { Length = checked((ushort)nameBytes), MaximumLength = checked((ushort)(nameBytes + sizeof(char))), Buffer = nameBuffer };
            Marshal.StructureToPtr(unicode, unicodeBuffer, fDeleteOld: false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(), ObjectName = unicodeBuffer, Attributes = ObjectAttributeCaseInsensitive };
            var status = NtCreateFile(out var rawHandle, access, ref attributes, out var ioStatus, IntPtr.Zero, FileAttributeNormal, FileShareRead | FileShareWrite | FileShareDelete, disposition, options, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            information = ioStatus.Information.ToInt64();
            if (status >= 0)
            {
                return new SafeFileHandle(rawHandle, ownsHandle: true);
            }

            var unsignedStatus = unchecked((uint)status);
            if (allowMissing && unsignedStatus is StatusObjectNameNotFound or StatusObjectPathNotFound)
            {
                return null;
            }

            throw new ContextualRoleNativeIOException($"NtCreateFile failed closed with NTSTATUS 0x{unsignedStatus:x8}.", ContextualRoleNativeErrorKind.NtStatus, unsignedStatus);
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    [ExcludeFromCodeCoverage]
    private static void RenameRelative(SafeFileHandle source, SafeFileHandle directory, string sourceName, string targetName, bool overwrite)
    {
        if (OperatingSystem.IsWindows())
        {
            var nameBytes = Encoding.Unicode.GetBytes(targetName);
            var pointerOffset = Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.RootDirectory)).ToInt32();
            var lengthOffset = Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.FileNameLength)).ToInt32();
            var nameOffset = Marshal.OffsetOf<FileRenameInformationHeader>(nameof(FileRenameInformationHeader.FileName)).ToInt32();
            var bufferSize = checked(Marshal.SizeOf<FileRenameInformationHeader>() + nameBytes.Length);
            var directoryReferenceAdded = false;
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                directory.DangerousAddRef(ref directoryReferenceAdded);
                for (var index = 0; index < bufferSize; index++)
                {
                    Marshal.WriteByte(buffer, index, 0);
                }

                Marshal.WriteInt32(buffer, overwrite ? 1 : 0);
                Marshal.WriteIntPtr(buffer, pointerOffset, directory.DangerousGetHandle());
                Marshal.WriteInt32(buffer, lengthOffset, nameBytes.Length);
                Marshal.Copy(nameBytes, 0, buffer + nameOffset, nameBytes.Length);
                if (!SetFileInformationByHandle(source, FileRenameInfo, buffer, checked((uint)bufferSize)))
                {
                    throw NativeIOException("SetFileInformationByHandle rename", Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                if (directoryReferenceAdded)
                {
                    directory.DangerousRelease();
                }
            }

            return;
        }

        var directoryDescriptor = directory.DangerousGetHandle().ToInt32();
        int result;
        if (overwrite)
        {
            result = UnixRenameAt(directoryDescriptor, sourceName, directoryDescriptor, targetName);
        }
        else if (OperatingSystem.IsLinux())
        {
            result = UnixRenameAt2(directoryDescriptor, sourceName, directoryDescriptor, targetName, UnixRenameNoReplace);
        }
        else if (OperatingSystem.IsMacOS())
        {
            result = MacRenameAtExclusive(directoryDescriptor, sourceName, directoryDescriptor, targetName, MacRenameExclusive);
        }
        else
        {
            throw new PlatformNotSupportedException("Handle-relative exclusive contextual-role publication is unavailable on this platform.");
        }

        if (result != 0)
        {
            throw NativeIOException("handle-relative rename", Marshal.GetLastPInvokeError());
        }
    }

    [ExcludeFromCodeCoverage]
    private static void DeleteExactTemporary(SafeFileHandle directory, string name, SafeFileHandle handle, NativeIdentity expected)
    {
        ValidateIdentity(handle, expected, requireSingleLink: true, "contextual-role temporary artifact");
        if (OperatingSystem.IsWindows())
        {
            var disposition = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(disposition, 1);
                if (!SetFileInformationByHandle(handle, FileDispositionInfo, disposition, 1))
                {
                    throw NativeIOException("SetFileInformationByHandle disposition", Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(disposition);
            }

            return;
        }

        using var named = OpenRelativeFile(directory, name, allowMissing: true, create: false, exclusiveCreate: false, write: true);
        if (named is null)
        {
            return;
        }

        var namedIdentity = ValidateRegularFile(named, "contextual-role named temporary artifact");
        if (!SameIdentity(namedIdentity, expected))
        {
            throw new InvalidOperationException("A contextual-role temporary filename was substituted before cleanup.");
        }

        if (UnixUnlinkAt(directory.DangerousGetHandle().ToInt32(), name, 0) != 0)
        {
            throw NativeIOException("unlinkat", Marshal.GetLastPInvokeError());
        }
    }

    [ExcludeFromCodeCoverage]
    private static void FlushFile(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!FlushFileBuffers(handle))
            {
                throw NativeIOException("FlushFileBuffers file barrier", Marshal.GetLastPInvokeError());
            }

            return;
        }

        var descriptor = handle.DangerousGetHandle().ToInt32();
        if (UnixFsync(descriptor) != 0)
        {
            throw NativeIOException("fsync file barrier", Marshal.GetLastPInvokeError());
        }

        if (OperatingSystem.IsMacOS() && UnixFcntl(descriptor, MacFullFsync) != 0)
        {
            throw NativeIOException("F_FULLFSYNC file barrier", Marshal.GetLastPInvokeError());
        }
    }

    [ExcludeFromCodeCoverage]
    private static void FlushDirectory(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows does not provide a portable directory-handle flush: NTFS rejects
            // NtFlushBuffersFile for directory handles on supported CI hosts. Directory
            // creation and rename are instead issued through retained handles opened with
            // FILE_WRITE_THROUGH, and published files are reopened by that retained parent
            // and flushed before acknowledgement. Requiring the unsupported directory call
            // would make every first mutation fail before an intent can be published.
            return;
        }

        if (UnixFsync(handle.DangerousGetHandle().ToInt32()) != 0)
        {
            throw new ContextualRolePersistenceUnavailableException($"A parent-directory fsync barrier is unavailable (errno {Marshal.GetLastPInvokeError()}); publication cannot be acknowledged.");
        }
    }

    [ExcludeFromCodeCoverage]
    private static NativeIdentity GetIdentity(SafeFileHandle handle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandle(handle, out var information))
            {
                throw NativeIOException("GetFileInformationByHandle", Marshal.GetLastPInvokeError());
            }

            var fileId = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
            var isDirectory = (information.FileAttributes & FileAttributeDirectory) != 0;
            var isReparsePoint = (information.FileAttributes & FileAttributeReparsePoint) != 0;
            return new NativeIdentity(information.VolumeSerialNumber, fileId, information.NumberOfLinks, isDirectory, !isDirectory && !isReparsePoint, isReparsePoint);
        }

        if (OperatingSystem.IsLinux())
        {
            if (LinuxStatx(handle.DangerousGetHandle().ToInt32(), string.Empty, LinuxAtEmptyPath | LinuxAtNoAutomount, LinuxStatxBasicStats, out var statx) != 0)
            {
                throw NativeIOException("statx", Marshal.GetLastPInvokeError());
            }

            var device = ((ulong)statx.DeviceMajor << 32) | statx.DeviceMinor;
            return new NativeIdentity(device, statx.Inode, statx.LinkCount, (statx.Mode & UnixFileTypeMask) == UnixDirectoryType, (statx.Mode & UnixFileTypeMask) == UnixRegularFileType, (statx.Mode & UnixFileTypeMask) == UnixSymbolicLinkType);
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Retained contextual-role artifact identity is supported only on Windows, Linux, and macOS.");
        }

        var buffer = Marshal.AllocHGlobal(MacStatBufferBytes);
        try
        {
            for (var index = 0; index < MacStatBufferBytes; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            if (UnixFstat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
            {
                throw NativeIOException("fstat", Marshal.GetLastPInvokeError());
            }

            var device = unchecked((uint)Marshal.ReadInt32(buffer, 0));
            var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
            var links = unchecked((ushort)Marshal.ReadInt16(buffer, 6));
            var file = unchecked((ulong)Marshal.ReadInt64(buffer, 8));
            return new NativeIdentity(device, file, links, (mode & UnixFileTypeMask) == UnixDirectoryType, (mode & UnixFileTypeMask) == UnixRegularFileType, (mode & UnixFileTypeMask) == UnixSymbolicLinkType);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static IOException NativeIOException(string operation, int error)
    {
        var errorKind = OperatingSystem.IsWindows() ? ContextualRoleNativeErrorKind.Win32 : ContextualRoleNativeErrorKind.PosixErrno;
        return new ContextualRoleNativeIOException($"{operation} failed closed with native error {error}.", errorKind, error, new Win32Exception(error));
    }

    private static bool SameIdentity(NativeIdentity left, NativeIdentity right) => left.Device == right.Device && left.File == right.File;

    private readonly record struct NativeIdentity(ulong Device, ulong File, uint LinkCount, bool IsDirectory, bool IsRegularFile, bool IsReparsePoint);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr file, uint desiredAccess, ref ObjectAttributes objectAttributes, out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryDirectoryFile(SafeFileHandle file, IntPtr eventHandle, IntPtr apcRoutine, IntPtr apcContext, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass, byte returnSingleEntry, IntPtr fileName, byte restartScan);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int UnixOpenAt(int directory, string path, int flags, int mode);

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int UnixMkdirAt(int directory, string path, uint mode);

    [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
    private static extern int UnixRenameAt(int oldDirectory, string oldPath, int newDirectory, string newPath);

    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int UnixRenameAt2(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int MacRenameAtExclusive(int oldDirectory, string oldPath, int newDirectory, string newPath, uint flags);

    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnixUnlinkAt(int directory, string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int UnixFsync(int descriptor);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int UnixFcntl(int descriptor, int command);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int UnixFstat(int descriptor, IntPtr buffer);

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int LinuxStatx(int directory, string path, int flags, uint mask, out LinuxStatxBuffer buffer);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int UnixFchmod(int descriptor, int mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int UnixClose(int descriptor);

    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int UnixDuplicate(int descriptor);

    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr UnixFdOpenDirectory(int descriptor);

    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr UnixReadDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "rewinddir")]
    private static extern void UnixRewindDirectory(IntPtr directory);

    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)]
    private static extern int UnixCloseDirectory(IntPtr directory);

    private static int UnixDirectory => OperatingSystem.IsMacOS() ? 0x00100000 : 0x00010000;
    private static int UnixNoFollow => OperatingSystem.IsMacOS() ? 0x00000100 : 0x00020000;
    private static int UnixCloseOnExec => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
    private static int UnixCreate => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;
    private static int UnixExclusive => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;
    private static int UnixNonBlocking => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;
    private const int UnixReadOnly = 0;
    private const int UnixReadWrite = 2;
    private const int UnixNoEntry = 2;
    private const int UnixNotDirectory = 20;
    private const int UnixAlreadyExists = 17;
    private const uint UnixRenameNoReplace = 1;
    private const uint MacRenameExclusive = 4;
    private const int MacFullFsync = 51;
    private const int MacStatBufferBytes = 256;
    private const int LinuxAtNoAutomount = 0x800;
    private const int LinuxAtEmptyPath = 0x1000;
    private const uint LinuxStatxBasicStats = 0x07ff;
    private const uint UnixFileTypeMask = 0xF000;
    private const uint UnixDirectoryType = 0x4000;
    private const uint UnixRegularFileType = 0x8000;
    private const uint UnixSymbolicLinkType = 0xA000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint SynchronizeAccess = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const uint ObjectAttributeCaseInsensitive = 0x00000040;
    private const uint NtOpen = 1;
    private const uint NtCreate = 2;
    private const uint NtOpenIf = 3;
    private const long NtCreated = 2;
    private const uint NtDirectoryFile = 0x00000001;
    private const uint NtWriteThrough = 0x00000002;
    private const uint NtSynchronousIoNonAlert = 0x00000020;
    private const uint NtNonDirectoryFile = 0x00000040;
    private const uint NtOpenReparsePoint = 0x00200000;
    private const uint StatusObjectNameNotFound = 0xC0000034;
    private const uint StatusObjectPathNotFound = 0xC000003A;
    private const uint StatusNoMoreFiles = 0x80000006;
    private const int FileDirectoryInformation = 1;
    private const int WindowsFileDirectoryInformationNameOffset = 64;
    private const int WindowsMaximumFileNameBytes = 510;
    private const int UnixDirectoryRecordLengthOffset = 16;
    private const int LinuxDirectoryNameOffset = 19;
    private const int MacDirectoryNameOffset = 21;
    private const int UnixMaximumFileNameBytes = 255;
    private const int UnixMaximumDirectoryRecordBytes = 1_280;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private static int UnixSymbolicLinkLoop => OperatingSystem.IsMacOS() ? 62 : 40;
}
