using System.Text;

namespace EmbodySense.Core.Persistence.Loops;

/// <summary>
/// Commits a text artifact by writing a sibling temporary file and renaming it over the destination.
/// </summary>
/// <remarks>
/// The same-directory rename is the visibility boundary; this helper does not provide a transaction spanning multiple files.
/// Cancellation and I/O failures propagate. The ordinary writer removes its uncommitted temporary file, while the proof-bearing
/// writer conservatively retains an ambiguous staging pathname after failure so cleanup cannot unlink a replacement artifact.
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

    /// <summary>
    /// Stages and atomically replaces one text artifact while capturing proof from the exact open staging object.
    /// </summary>
    /// <typeparam name="TProof">The captured proof type.</typeparam>
    /// <param name="path">The destination artifact path.</param>
    /// <param name="content">The complete text artifact content.</param>
    /// <param name="captureProof">Captures proof from the flushed staging handle and the exact encoded bytes before publication.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The proof captured for the staging object that the writer attempted to publish.</returns>
    public static async Task<TProof> WriteTextWithProofAsync<TProof>(
        string path,
        string content,
        Func<FileStream, byte[], TProof> captureProof,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(captureProof);

        var directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var bytes = Encoding.UTF8.GetBytes(content);
        TProof proof;
        await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous))
        {
            await stream.WriteAsync(bytes, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            proof = captureProof(stream, bytes);
            File.Move(tempPath, path, overwrite: true);
        }

        return proof;
    }
}
