using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Memory;

/// <summary>
/// Acquires exclusive cross-process ownership of one workspace conversation turn.
/// </summary>
/// <remarks>
/// Every <see cref="IOException"/> raised while opening the lock is treated as transient contention and retried at a bounded
/// polling interval until cancellation. The returned stream is the ownership token and must be disposed to release the lease.
/// Authorization and directory-creation failures propagate.
/// </remarks>
public sealed class FileConversationWorkspaceLease : IConversationWorkspaceLease
{
    private static readonly TimeSpan _retryDelay = TimeSpan.FromMilliseconds(25);
    private readonly string _lockPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="FileConversationWorkspaceLease"/> type.
    /// </summary>
    /// <param name="paths">The paths.</param>
    public FileConversationWorkspaceLease(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _lockPath = paths.ConversationTurnLockPath;
    }

    /// <summary>
    /// Waits until the exclusive conversation-turn lock file can be opened.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>The disposable ownership stream.</returns>
    public async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(_retryDelay, cancellationToken);
            }
        }
    }
}
