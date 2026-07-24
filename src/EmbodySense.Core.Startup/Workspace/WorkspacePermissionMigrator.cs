using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

internal static class WorkspacePermissionMigrator
{
    private const long MaxMigratedPermissionsUtf8Bytes = 128 * 1024;
    private const int MaxExclusiveOpenAttempts = 80;
    private static readonly TimeSpan ExclusiveOpenRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task MigrateAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        for (var attempt = 1; attempt <= MaxExclusiveOpenAttempts; attempt++)
        {
            var inspected = await ReadCurrentAsync(paths.PermissionsPath, cancellationToken);
            if (inspected is null)
            {
                return;
            }

            var migratedPermissions = ParseMigration(inspected);
            if (migratedPermissions is null)
            {
                return;
            }

            FileStream source;
            try
            {
                source = new FileStream(paths.PermissionsPath, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (attempt < MaxExclusiveOpenAttempts)
            {
                await Task.Delay(ExclusiveOpenRetryDelay, cancellationToken);
                continue;
            }

            await using (source)
            {
                var lockedSource = await ReadBoundedAsync(source, cancellationToken);
                if (!lockedSource.AsSpan().SequenceEqual(inspected))
                {
                    if (attempt == MaxExclusiveOpenAttempts)
                    {
                        throw new IOException("The permissions policy kept changing while its migration was being prepared.");
                    }

                    continue;
                }

                await CommitMigrationAsync(paths, source, lockedSource, migratedPermissions, cancellationToken);
                return;
            }
        }
    }

    private static async Task CommitMigrationAsync(WorkspacePaths paths, FileStream source, byte[] lockedSource, PermissionsDocument migratedPermissions, CancellationToken cancellationToken)
    {
        var migrated = Utf8.GetBytes(migratedPermissions.ToJson() + Environment.NewLine);
        var operationId = Guid.NewGuid().ToString("N");
        var temporaryPath = paths.PermissionsPath + "." + operationId + ".migration";
        var backupPath = paths.PermissionsPath + "." + operationId + ".migration-backup";
        var rollbackPath = paths.PermissionsPath + "." + operationId + ".migration-rollback";
        UnixFileMode? unixMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(paths.PermissionsPath);
        try
        {
            await using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await temporary.WriteAsync(migrated, cancellationToken);
                await temporary.FlushAsync(cancellationToken);
                temporary.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporaryPath, unixMode!.Value);
            }

            await source.DisposeAsync();
            var current = await ReadCurrentAsync(paths.PermissionsPath, cancellationToken)
                ?? throw new IOException("The permissions policy disappeared before migration could commit.");
            if (!current.AsSpan().SequenceEqual(lockedSource))
            {
                throw new IOException("The permissions policy changed before migration could commit.");
            }

            // ReplaceFile preserves the destination's Windows security descriptor; the source mode is copied explicitly on Unix.
            File.Replace(temporaryPath, paths.PermissionsPath, backupPath, ignoreMetadataErrors: false);
            var displaced = await ReadCurrentAsync(backupPath, cancellationToken)
                ?? throw new IOException("The permissions migration could not verify the displaced source policy.");
            if (!displaced.AsSpan().SequenceEqual(lockedSource))
            {
                await RestoreConcurrentReplacementAsync(paths.PermissionsPath, backupPath, rollbackPath, migrated, cancellationToken);
                throw new IOException("The permissions policy changed during migration; the concurrent policy was restored.");
            }
        }
        finally
        {
            DeleteIfPresent(temporaryPath);
            DeleteIfPresent(backupPath);
            DeleteIfPresent(rollbackPath);
        }
    }

    private static PermissionsDocument? ParseMigration(byte[] source)
    {
        PermissionsDocument? permissions;
        try
        {
            using var stream = new MemoryStream(source, writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            permissions = PermissionsDocument.FromJson(reader.ReadToEnd());
        }
        catch (JsonException)
        {
            return null;
        }

        return permissions?.MigrateToolResponseInspectionPolicy();
    }

    private static async Task<byte[]?> ReadCurrentAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await ReadBoundedAsync(stream, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(FileStream source, CancellationToken cancellationToken)
    {
        if (source.Length < 1)
        {
            return [];
        }

        if (source.Length > MaxMigratedPermissionsUtf8Bytes)
        {
            throw new IOException($"The permissions policy exceeds the {MaxMigratedPermissionsUtf8Bytes}-byte migration safety limit and cannot be accepted without an explicit policy update.");
        }

        source.Position = 0;
        var bytes = new byte[checked((int)source.Length)];
        await source.ReadExactlyAsync(bytes, cancellationToken);
        return bytes;
    }

    private static async Task RestoreConcurrentReplacementAsync(string permissionsPath, string backupPath, string rollbackPath, byte[] migrated, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(permissionsPath, cancellationToken);
        if (current is not null && current.AsSpan().SequenceEqual(migrated))
        {
            File.Replace(backupPath, permissionsPath, rollbackPath, ignoreMetadataErrors: false);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
