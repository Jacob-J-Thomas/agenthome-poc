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

        FileStream stream;
        try
        {
            stream = new FileStream(paths.PermissionsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        await using (stream)
        {
            if (stream.Length < 1 || stream.Length > MaxMigratedPermissionsUtf8Bytes)
            {
                return;
            }

            string json;
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
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

            if (permissions?.EnsureToolResponseInspectionApproval() != true)
            {
                return;
            }

            var migrated = Utf8.GetBytes(permissions.ToJson() + Environment.NewLine);
            stream.Position = 0;
            await stream.WriteAsync(migrated, cancellationToken);
            stream.SetLength(migrated.LongLength);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }
    }
}
