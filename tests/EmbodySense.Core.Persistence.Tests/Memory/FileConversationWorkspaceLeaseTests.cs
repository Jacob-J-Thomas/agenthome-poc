using EmbodySense.Core.Common.Workspace;
using EmbodySense.Core.Persistence.Memory;
using EmbodySense.Tests.Support;

namespace EmbodySense.Core.Persistence.Tests.Memory;

public sealed class FileConversationWorkspaceLeaseTests
{
    [Fact]
    public async Task Lease_blocks_other_file_owners_until_release_and_honors_cancellation()
    {
        using var workspace = new TestWorkspace();
        var paths = new WorkspacePaths(workspace.RootPath);
        var firstProvider = new FileConversationWorkspaceLease(paths);
        var secondProvider = new FileConversationWorkspaceLease(paths);
        var first = await firstProvider.AcquireAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondProvider.AcquireAsync(cancellation.Token));
        Assert.True(File.Exists(paths.ConversationTurnLockPath));

        first.Dispose();
        using var second = await secondProvider.AcquireAsync();
        Assert.Throws<IOException>(() => new FileStream(paths.ConversationTurnLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read));
    }
}
