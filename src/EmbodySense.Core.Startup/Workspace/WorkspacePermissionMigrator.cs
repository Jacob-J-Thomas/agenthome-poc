using System.Text;
using System.Text.Json;
using EmbodySense.Core.Common.Governance.Permissions.Models;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Startup.Workspace;

internal static class WorkspacePermissionMigrator
{
    private const long MaxMigratedPermissionsUtf8Bytes = 128 * 1024;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task MigrateAsync(WorkspacePaths paths, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        FileStream source;
        try
        {
            var share = OperatingSystem.IsWindows() ? FileShare.Delete : FileShare.None;
            source = new FileStream(paths.PermissionsPath, FileMode.Open, FileAccess.ReadWrite, share, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        await using (source)
        {
            if (source.Length < 1 || source.Length > MaxMigratedPermissionsUtf8Bytes)
            {
                return;
            }

            string json;
            using (var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
            {
                json = await reader.ReadToEndAsync(cancellationToken);
            }

            PermissionsDocument? permissions;
            try
            {
                permissions = PermissionsDocument.FromJson(json);
            }
            catch (JsonException)
            {
                return;
            }

            var migratedPermissions = permissions?.MigrateToolResponseInspectionPolicy();
            if (migratedPermissions is null)
            {
                return;
            }

            var migrated = Utf8.GetBytes(migratedPermissions.ToJson() + Environment.NewLine);
            var temporaryPath = paths.PermissionsPath + "." + Guid.NewGuid().ToString("N") + ".migration";
            try
            {
                await using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await temporary.WriteAsync(migrated, cancellationToken);
                    await temporary.FlushAsync(cancellationToken);
                    temporary.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, paths.PermissionsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
