using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EmbodySense.Core.Startup.Inference.Profiles;

/// <summary>Binds one fresh model client to an immutable, private runtime-package snapshot for the lifetime of its process.</summary>
internal sealed class ConfiguredModelExecutableSnapshotLease : IAsyncDisposable
{
    private const int MaximumPackageFiles = 4_096;
    private const int MaximumPackageDirectories = 1_024;
    private const int MaximumPackageRootSearchDepth = 4;
    private const int MaximumSiblingCandidates = 1_024;
    private const int MaximumDependencyManifestCharacters = 1_000_000;
    private const int MaximumScavengeCandidates = 32;
    private const string LeaseDirectoryPrefix = "lease-";
    private const string LeaseFileName = ".embodysense-model-profile-lease";
    private static readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "embodysense-model-profile-snapshots");
    private FileStream? _leaseLock;
    private string? _snapshotDirectory;

    private ConfiguredModelExecutableSnapshotLease(
        string executablePath,
        string artifactHash,
        string snapshotDirectory,
        FileStream leaseLock)
    {
        ExecutablePath = executablePath;
        ArtifactHash = artifactHash;
        _snapshotDirectory = snapshotDirectory;
        _leaseLock = leaseLock;
    }

    internal string ExecutablePath { get; }

    internal string ArtifactHash { get; }

    internal static string ResolveExactExecutablePath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var exactPath = Path.GetFullPath(sourcePath);
        var file = new FileInfo(exactPath);
        if (!file.Exists)
        {
            throw new ArgumentException("The configured model executable is unavailable.", nameof(sourcePath));
        }

        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            file = file.ResolveLinkTarget(returnFinalTarget: true) as FileInfo
                ?? throw new ArgumentException("The configured model executable link target is unavailable.", nameof(sourcePath));
        }

        return Path.GetFullPath(file.FullName);
    }

    internal static string ReadSourceContentHash(string sourcePath, long maximumBytes)
    {
        var package = DescribePackage(sourcePath, maximumBytes);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPackageHeader(hash, package);
        var buffer = new byte[64 * 1024];
        foreach (var file in package.Files)
        {
            AppendFileHeader(hash, file);
            using var source = OpenSource(file.Path);
            AppendStream(hash, source, buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static async Task<ConfiguredModelExecutableSnapshotLease> AcquireAsync(
        string sourcePath,
        string expectedContentHash,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedContentHash);
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var package = DescribePackage(sourcePath, maximumBytes);
        ScavengeOrphanedSnapshots();
        var snapshotDirectory = Path.Combine(_snapshotRoot, LeaseDirectoryPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapshotDirectory);
        EnsureOwnerOnlyDirectory(snapshotDirectory);
        FileStream? leaseLock = null;
        try
        {
            var lockPath = Path.Combine(snapshotDirectory, LeaseFileName);
            leaseLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            leaseLock.WriteByte(1);
            leaseLock.Flush(flushToDisk: true);
            TryLock(leaseLock);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendPackageHeader(hash, package);
            var buffer = new byte[64 * 1024];
            foreach (var file in package.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendFileHeader(hash, file);
                var destinationPath = Path.Combine(snapshotDirectory, file.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("The configured model runtime package produced an invalid snapshot path.");
                Directory.CreateDirectory(destinationDirectory);
                EnsureOwnerOnlyDirectory(destinationDirectory);
                await using var source = OpenSource(file.Path);
                await using var destination = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                int read;
                long copied = 0;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied = checked(copied + read);
                    if (copied > file.Length)
                    {
                        throw new InvalidOperationException("The configured model runtime package changed during exact snapshot acquisition.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                if (copied != file.Length)
                {
                    throw new InvalidOperationException("The configured model runtime package changed during exact snapshot acquisition.");
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(destinationPath, ReadOnlySnapshotMode(file.UnixMode));
                }
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!IsCanonicalHash(expectedContentHash)
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(expectedContentHash)))
            {
                throw new InvalidOperationException("The configured model runtime package changed before exact client resolution.");
            }

            var executablePath = Path.Combine(snapshotDirectory, package.EntryPointRelativePath);
            var result = new ConfiguredModelExecutableSnapshotLease(
                executablePath,
                actualHash,
                snapshotDirectory,
                leaseLock);
            leaseLock = null;
            return result;
        }
        finally
        {
            if (leaseLock is not null)
            {
                TryUnlock(leaseLock);
                await leaseLock.DisposeAsync().ConfigureAwait(false);
                TryDeleteDirectory(snapshotDirectory);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var leaseLock = Interlocked.Exchange(ref _leaseLock, null);
        var snapshotDirectory = Interlocked.Exchange(ref _snapshotDirectory, null);
        if (leaseLock is null || snapshotDirectory is null)
        {
            return;
        }

        TryUnlock(leaseLock);
        await leaseLock.DisposeAsync().ConfigureAwait(false);
        TryDeleteDirectory(snapshotDirectory);
    }

    private static RuntimePackage DescribePackage(string sourcePath, long maximumBytes)
    {
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var executablePath = ResolveExactExecutablePath(sourcePath);
        var executableDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("The configured model runtime package directory is unavailable.");
        var packageRoot = FindPackageRoot(executableDirectory);
        var referencedPackageRoot = packageRoot is null && IsScript(executablePath)
            ? FindReferencedCodexPackageRoot(executablePath, executableDirectory)
            : null;
        var effectiveRoot = packageRoot ?? executableDirectory;
        var executable = DescribeFile(executablePath, effectiveRoot);
        IReadOnlyList<RuntimePackageFile> files;
        if (packageRoot is not null)
        {
            files = DescribePackageTree(packageRoot, executablePath, packageRoot);
        }
        else if (referencedPackageRoot is not null)
        {
            files = Array.AsReadOnly(
                new[] { executable }
                    .Concat(DescribePackageTree(referencedPackageRoot, requiredPath: null, executableDirectory))
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .ToArray());
        }
        else if (IsScript(executablePath))
        {
            var candidates = Directory.EnumerateFiles(executableDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !string.Equals(Path.GetFileName(path), LeaseFileName, StringComparison.Ordinal))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .Take(MaximumSiblingCandidates + 1)
                .Select(path => DescribeFile(path, executableDirectory))
                .ToArray();
            if (candidates.Length is 0 or > MaximumSiblingCandidates
                || !candidates.Any(file => PathsEqual(file.Path, executablePath)))
            {
                throw new InvalidOperationException("The configured script runtime package directory exceeded the bounded exact-artifact contract.");
            }

            files = SelectScriptClosure(executable, candidates);
        }
        else
        {
            files = [executable];
        }

        long totalBytes = 0;
        foreach (var file in files)
        {
            totalBytes = checked(totalBytes + file.Length);
            if (file.Length < 1 || totalBytes > maximumBytes)
            {
                throw new InvalidOperationException("The configured model runtime package exceeded the bounded exact-artifact contract.");
            }
        }

        return new RuntimePackage(executable.RelativePath, Array.AsReadOnly(files.ToArray()), totalBytes);
    }

    private static string? FindReferencedCodexPackageRoot(string executablePath, string executableDirectory)
    {
        var file = new FileInfo(executablePath);
        if (file.Length is < 1 or > MaximumDependencyManifestCharacters)
        {
            throw new InvalidOperationException("The configured model launcher exceeded the bounded exact-artifact contract.");
        }

        var text = File.ReadAllText(executablePath);
        if (text.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("The configured model launcher contained invalid binary content.");
        }

        var normalized = text.Replace('\\', '/');
        if (normalized.IndexOf("node_modules/@openai/codex/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var packageRoot = Path.Combine(executableDirectory, "node_modules", "@openai", "codex");
        var manifest = Path.Combine(packageRoot, "package.json");
        return Directory.Exists(packageRoot) && File.Exists(manifest) && IsExactCodexPackageManifest(manifest)
            ? packageRoot
            : throw new InvalidOperationException("The configured Codex npm launcher referenced an unavailable package tree.");
    }

    private static string? FindPackageRoot(string executableDirectory)
    {
        var current = new DirectoryInfo(executableDirectory);
        for (var depth = 0; current is not null && depth <= MaximumPackageRootSearchDepth; depth++, current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var manifest = Path.Combine(current.FullName, "package.json");
            if (File.Exists(manifest) && IsExactCodexPackageManifest(manifest))
            {
                return current.FullName;
            }
        }

        return null;
    }

    private static bool IsExactCodexPackageManifest(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 1 or > MaximumDependencyManifestCharacters || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The configured Codex npm package manifest exceeded the bounded exact-artifact contract or contained a link.");
        }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(file.FullName), new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }
            var names = document.RootElement.EnumerateObject().Where(property => string.Equals(property.Name, "name", StringComparison.Ordinal)).ToArray();
            return names.Length == 1
                && names[0].Value.ValueKind == JsonValueKind.String
                && string.Equals(names[0].Value.GetString(), "@openai/codex", StringComparison.Ordinal);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The configured Codex npm package manifest was malformed.", exception);
        }
    }

    private static IReadOnlyList<RuntimePackageFile> DescribePackageTree(string packageRoot, string? requiredPath, string relativeRoot)
    {
        var files = new List<RuntimePackageFile>();
        var pending = new Queue<DirectoryInfo>();
        pending.Enqueue(new DirectoryInfo(packageRoot));
        var directories = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            directories = checked(directories + 1);
            if (directories > MaximumPackageDirectories || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException("The configured npm runtime package directory exceeded the bounded exact-artifact contract or contained a link.");
            }

            foreach (var child in directory.EnumerateDirectories().OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                pending.Enqueue(child);
            }
            foreach (var file in directory.EnumerateFiles().OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (string.Equals(file.Name, LeaseFileName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (files.Count == MaximumPackageFiles)
                {
                    throw new InvalidOperationException("The configured npm runtime package exceeded the bounded exact-artifact file contract.");
                }
                files.Add(DescribeFile(file.FullName, relativeRoot));
            }
        }

        if (requiredPath is not null && !files.Any(file => PathsEqual(file.Path, requiredPath)))
        {
            throw new InvalidOperationException("The configured npm runtime package did not contain its exact entry point.");
        }
        return Array.AsReadOnly(files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static IReadOnlyList<RuntimePackageFile> SelectScriptClosure(
        RuntimePackageFile executable,
        IReadOnlyList<RuntimePackageFile> candidates)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var selected = new Dictionary<string, RuntimePackageFile>(StringComparer.Ordinal);
        var pending = new Queue<RuntimePackageFile>();
        selected.Add(executable.RelativePath, executable);
        pending.Enqueue(executable);
        while (pending.Count > 0)
        {
            var manifest = pending.Dequeue();
            if (manifest.Length > MaximumDependencyManifestCharacters)
            {
                continue;
            }

            var text = File.ReadAllText(manifest.Path);
            if (text.IndexOf('\0') >= 0)
            {
                continue;
            }

            foreach (var candidate in candidates)
            {
                if (selected.ContainsKey(candidate.RelativePath)
                    || text.IndexOf(Path.GetFileName(candidate.RelativePath), comparison) < 0)
                {
                    continue;
                }

                if (selected.Count == MaximumPackageFiles)
                {
                    throw new InvalidOperationException("The configured script runtime dependency closure exceeded the bounded exact-artifact contract.");
                }

                selected.Add(candidate.RelativePath, candidate);
                pending.Enqueue(candidate);
            }
        }

        return Array.AsReadOnly(selected.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private static RuntimePackageFile DescribeFile(string path, string packageRoot)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException("The configured model runtime package contains an unavailable or linked artifact.");
        }

        var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(file.FullName);
        var relativePath = Path.GetRelativePath(packageRoot, file.FullName);
        if (relativePath is "." or ".." || Path.IsPathRooted(relativePath) || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The configured model runtime package contained an artifact outside its exact root.");
        }
        return new RuntimePackageFile(relativePath, file.FullName, file.Length, mode);
    }

    private static bool IsScript(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var source = OpenSource(path);
        return source.ReadByte() == '#' && source.ReadByte() == '!';
    }

    private static FileStream OpenSource(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);

    private static void AppendPackageHeader(IncrementalHash hash, RuntimePackage package)
    {
        AppendToken(hash, "embodysense.configured-model-runtime-package.v2");
        AppendToken(hash, NormalizeRelativePath(package.EntryPointRelativePath));
        AppendToken(hash, package.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendToken(hash, package.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendFileHeader(IncrementalHash hash, RuntimePackageFile file)
    {
        AppendToken(hash, NormalizeRelativePath(file.RelativePath));
        AppendToken(hash, file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendToken(hash, file.UnixMode.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendToken(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private static void AppendStream(IncrementalHash hash, Stream source, byte[] buffer)
    {
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    private static bool IsCanonicalHash(string value)
        => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static UnixFileMode ReadOnlySnapshotMode(int originalMode)
    {
        var mode = (UnixFileMode)originalMode;
        mode &= ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        mode &= ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite);
        mode |= UnixFileMode.UserRead;
        return mode;
    }

    private static void EnsurePrivateSnapshotRoot()
    {
        Directory.CreateDirectory(_snapshotRoot);
        EnsureOwnerOnlyDirectory(_snapshotRoot);
    }

    private static void EnsureOwnerOnlyDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    internal static void ScavengeOrphanedSnapshots()
    {
        EnsurePrivateSnapshotRoot();
        var candidates = new DirectoryInfo(_snapshotRoot)
            .EnumerateDirectories(LeaseDirectoryPrefix + "*", SearchOption.TopDirectoryOnly)
            .Where(directory => (directory.Attributes & FileAttributes.ReparsePoint) == 0)
            .OrderBy(directory => directory.CreationTimeUtc)
            .Take(MaximumScavengeCandidates)
            .ToArray();
        foreach (var candidate in candidates)
        {
            var lockPath = Path.Combine(candidate.FullName, LeaseFileName);
            FileStream? probe = null;
            try
            {
                probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
                TryLock(probe);
                TryUnlock(probe);
                probe.Dispose();
                probe = null;
                TryDeleteDirectory(candidate.FullName);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                probe?.Dispose();
            }
        }
    }

    private static void TryLock(FileStream stream)
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            stream.Lock(0, 1);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static void TryUnlock(FileStream stream)
    {
        if (OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            stream.Unlock(0, 1);
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }

    private static string NormalizeRelativePath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private sealed record RuntimePackage(string EntryPointRelativePath, IReadOnlyList<RuntimePackageFile> Files, long TotalBytes);

    private sealed record RuntimePackageFile(string RelativePath, string Path, long Length, int UnixMode);
}
