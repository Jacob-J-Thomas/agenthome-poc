using System.Text.Json;

namespace EmbodySense.Core.Startup.Workspace;

/// <summary>Reads and writes the bounded version-one marker that proves workspace scaffolding and its completion audit succeeded.</summary>
internal static class WorkspaceInitializationCompletion
{
    private const int MaximumMarkerUtf8Bytes = 256;
    private const string MarkerJson = "{\"schemaVersion\":1,\"status\":\"completed\"}\n";

    public static async Task WriteAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await File.WriteAllTextAsync(path, MarkerJson, cancellationToken);
    }

    public static bool IsValid(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MaximumMarkerUtf8Bytes + 1,
                FileOptions.SequentialScan);
            var bytes = new byte[MaximumMarkerUtf8Bytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            if (length is <= 0 or > MaximumMarkerUtf8Bytes)
            {
                return false;
            }

            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2)
            {
                return false;
            }

            return root.TryGetProperty("schemaVersion", out var schemaVersion)
                && schemaVersion.ValueKind == JsonValueKind.Number
                && schemaVersion.TryGetInt32(out var value)
                && value == 1
                && root.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), "completed", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

}
