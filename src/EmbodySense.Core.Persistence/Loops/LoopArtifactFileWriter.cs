namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Commits a text artifact by writing a sibling temporary file and renaming it over the destination.
/// </summary>
/// <remarks>
/// The same-directory rename is the visibility boundary; this helper does not provide a transaction spanning multiple files.
/// Cancellation and I/O failures propagate, and an uncommitted temporary file is removed in the cleanup path.
/// </remarks>
internal static class LoopArtifactFileWriter
{
    /// <summary>
    /// Stages and atomically replaces one text artifact.
    /// </summary>
    /// <param name="path">The destination artifact path.</param>
    /// <param name="content">The complete text artifact content.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    public static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
