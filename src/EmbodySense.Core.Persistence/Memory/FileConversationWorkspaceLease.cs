using EmbodySense.Core.Application.Memory;
using EmbodySense.Core.Common.Workspace;

namespace EmbodySense.Core.Persistence.Memory;

public sealed class FileConversationWorkspaceLease : IConversationWorkspaceLease
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(25);
    private readonly string _lockPath;

    public FileConversationWorkspaceLease(WorkspacePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _lockPath = paths.ConversationTurnLockPath;
    }

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
                await Task.Delay(RetryDelay, cancellationToken);
            }
        }
    }
}
